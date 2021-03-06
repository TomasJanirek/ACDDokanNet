﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azi.Amazon.CloudDrive;
using Azi.Amazon.CloudDrive.JsonObjects;
using Azi.Tools;
using Newtonsoft.Json;

namespace Azi.ACDDokanNet
{
    public enum FailReason
    {
        ZeroLength,
        NoNode,
        Conflict
    }

    public class UploadService : IDisposable
    {
        public const string UploadFolder = "Upload";

        private const int ReuploadDelay = 5000;
        private readonly SemaphoreSlim uploadLimitSemaphore;

        private readonly BlockingCollection<UploadInfo> uploads = new BlockingCollection<UploadInfo>();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly AmazonDrive amazon;
        private readonly int uploadLimit;
        private string cachePath;
        private bool disposedValue = false; // To detect redundant calls
        private Task serviceTask;

        public UploadService(int limit, AmazonDrive amazon)
        {
            uploadLimit = limit;
            uploadLimitSemaphore = new SemaphoreSlim(limit);
            this.amazon = amazon;
        }

        public delegate void OnUploadFinishedDelegate(UploadInfo item, AmazonNode amazonNode);

        public delegate void OnUploadFailedDelegate(UploadInfo item, FailReason reason);

        public OnUploadFinishedDelegate OnUploadFinished { get; set; }

        public OnUploadFailedDelegate OnUploadFailed { get; set; }

        public Action<FSItem> OnUploadResumed { get; set; }

        public string CachePath
        {
            get
            {
                return cachePath;
            }

            set
            {
                var newpath = Path.Combine(value, UploadFolder);
                if (cachePath == newpath)
                {
                    return;
                }

                Log.Trace($"Cache path changed from {cachePath} to {newpath}");
                cachePath = newpath;
                Directory.CreateDirectory(cachePath);
                CheckOldUploads();
            }
        }

        public void AddOverwrite(FSItem item)
        {
            var info = new UploadInfo(item)
            {
                Overwrite = true
            };

            var path = Path.Combine(cachePath, item.Id);
            WriteInfo(path + ".info", info);
            uploads.Add(info);
        }

        public NewFileBlockWriter OpenNew(FSItem item)
        {
            var path = Path.Combine(cachePath, item.Id);
            var result = new NewFileBlockWriter(item, path);
            result.OnClose = () =>
              {
                  AddUpload(item);
              };

            return result;
        }

        public NewFileBlockWriter OpenTruncate(FSItem item)
        {
            var path = Path.Combine(cachePath, item.Id);
            var result = new NewFileBlockWriter(item, path);
            result.SetLength(0);
            result.OnClose = () =>
            {
                AddOverwrite(item);
            };

            return result;
        }

        public void Stop()
        {
            if (serviceTask == null)
            {
                return;
            }

            cancellation.Cancel();
            try
            {
                serviceTask.Wait();
            }
            catch (AggregateException e)
            {
                e.Handle(ce => ce is TaskCanceledException);
            }

            serviceTask = null;
        }

        public void Start()
        {
            if (serviceTask != null)
            {
                return;
            }

            serviceTask = Task.Factory.StartNew(() => UploadTask(), cancellation.Token, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);

            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }

        public void WaitForUploadsFnish()
        {
            while (uploads.Count > 0)
            {
                Thread.Sleep(100);
            }

            for (int i = 0; i < uploadLimit; i++)
            {
                uploadLimitSemaphore.Wait();
            }

            return;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Stop();
                    cancellation.Dispose();
                    uploadLimitSemaphore.Dispose();
                    uploads.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.
                disposedValue = true;
            }
        }

        private void WriteInfo(string path, UploadInfo info)
        {
            using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write)))
            {
                writer.Write(JsonConvert.SerializeObject(info));
            }
        }

        private void UploadTask()
        {
            UploadInfo upload;
            while (uploads.TryTake(out upload, -1, cancellation.Token))
            {
                var uploadCopy = upload;
                if (!uploadLimitSemaphore.Wait(-1, cancellation.Token))
                {
                    return;
                }

                Task.Run(async () => await Upload(uploadCopy));
            }
        }

        private void AddUpload(FSItem item)
        {
            var info = new UploadInfo(item);

            var path = Path.Combine(cachePath, item.Id);
            WriteInfo(path + ".info", info);
            uploads.Add(info);
        }

        private void CheckOldUploads()
        {
            var files = Directory.GetFiles(cachePath, "*.info");
            if (files.Length == 0)
            {
                return;
            }

            Log.Warn($"{files.Length} not uploaded files found. Resuming.");
            foreach (var info in files.Select(f => new FileInfo(f)).OrderBy(f => f.CreationTime))
            {
                var uploadinfo = JsonConvert.DeserializeObject<UploadInfo>(File.ReadAllText(info.FullName));
                var fileinfo = new FileInfo(Path.Combine(info.DirectoryName, Path.GetFileNameWithoutExtension(info.Name)));
                var item = FSItem.MakeUploading(uploadinfo.Path, fileinfo.Name, uploadinfo.ParentId, fileinfo.Length);
                OnUploadResumed(item);
                uploads.Add(uploadinfo);
            }
        }

        private async Task Upload(UploadInfo item)
        {
            var path = Path.Combine(cachePath, item.Id);
            try
            {
                if (item.Length == 0)
                {
                    Log.Trace("Zero Length file: " + item.Path);
                    File.Delete(path + ".info");
                    OnUploadFailed(item, FailReason.ZeroLength);
                    return;
                }

                Log.Trace("Started upload: " + item.Path);
                AmazonNode node;
                if (!item.Overwrite)
                {
                    node = await amazon.Files.UploadNew(
                        item.ParentId,
                        Path.GetFileName(item.Path),
                        () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true));
                }
                else
                {
                    node = await amazon.Files.Overwrite(
                        item.Id,
                        () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true));
                }

                File.Delete(path + ".info");
                if (node == null)
                {
                    OnUploadFailed(item, FailReason.NoNode);
                    throw new NullReferenceException("File node is null: " + item.Path);
                }

                OnUploadFinished(item, node);
                Log.Trace("Finished upload: " + item.Path + " id:" + node.id);
                return;
            }
            catch (HttpWebException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    Log.Warn($"Upload Conflict. Skip file: {item.Path}");
                    var node = await amazon.Nodes.GetChild(item.ParentId, Path.GetFileName(item.Path));
                    if (node != null)
                    {
                        OnUploadFinished(item, node);
                    }
                    else
                    {
                        OnUploadFailed(item, FailReason.Conflict);
                    }

                    return;
                }

                Log.Error($"Upload HTTP error: {item.Path}\r\n{ex}");
            }
            catch (Exception ex)
            {
                Log.Error($"Upload failed: {item.Path}\r\n{ex}");
            }
            finally
            {
                uploadLimitSemaphore.Release();
            }

            await Task.Delay(ReuploadDelay);
            uploads.Add(item);
        }

        public class UploadInfo
        {
            public UploadInfo()
            {
            }

            public UploadInfo(FSItem item)
            {
                Id = item.Id;
                Path = item.Path;
                ParentId = item.ParentIds.First();
                Length = item.Length;
            }

            public long Length { get; set; }

            public string Id { get; set; }

            public string Path { get; set; }

            public string ParentId { get; set; }

            public bool Overwrite { get; set; } = false;
        }
    }
}
