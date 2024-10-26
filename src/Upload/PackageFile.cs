// <copyright file="PackageFile.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;

namespace CSUploader.Upload
{
    public class PackageFile : PackageDetails
    {
        public PackageFile(Package package, string filePath, FileHosterClient fileHoster, FileHosterLoginDto fileHosterLoginDto)
        {
            Package = package;
            Name = Path.GetFileName(filePath);
            FileInfo = new FileInfo(filePath);

            FileHoster = fileHoster;
            FileHoster.UploadProgress += FileHoster_UploadProgress;
            FileHoster.UploadFinished += FileHoster_UploadFinished;
            FileHoster.HashingProgress += FileHoster_HashingProgress;
            FileHoster.HashingFinished += FileHoster_HashingFinished;

            FileHosterLogin = fileHosterLoginDto;
            SaveFrom = Path.GetDirectoryName(filePath);
            FileType = FileInfo.Extension[1..];
            BytesRemaining = Size;
        }

        /// <summary>
        /// Gets the total size of the archive package.
        /// </summary>
        public override long? Size => FileInfo?.Length;

        /// <summary>
        /// Gets the file hosters used the package is uploading to.
        /// </summary>
        public override FileHosterClient[] FileHosters => new FileHosterClient[] { FileHoster };

        /// <summary>
        /// Gets or sets the file hoster login information.
        /// </summary>
        public FileHosterLoginDto FileHosterLogin { get; set; }

        /// <summary>
        /// Gets or sets the file count of the package.
        /// </summary>
        public int? FileCount { get; set; }

        /// <summary>
        /// Gets or sets the URL to the file on the remote file hoster for downloading.
        /// </summary>
        public string? FileUrl { get; set; }

        /// <summary>
        /// Gets or sets the file type.
        /// </summary>
        public string FileType { get; set; }

        /// <summary>
        /// Gets the Package this instance belongs to.
        /// </summary>
        public Package Package { get; }

        /// <summary>
        /// Gets a value indicating whether hashing is required before uploading a file.
        /// </summary>
        public bool RequiresHashingBeforeUpload => FileHoster.RequiresHashingBeforeUpload;

        /// <summary>
        /// Gets a value indicating whether hashing is required after uploading a file has finished.
        /// </summary>
        public bool RequiresHashingAfterUpload => FileHoster.RequiresHashingAfterUpload;

        /// <summary>
        /// Gets a value indicating whether upload has finished.
        /// </summary>
        public bool IsUploadFinished { get; private set; }

        private FileInfo FileInfo { get; set; }

        private FileHosterClient FileHoster { get; set; }

        public override PackageJob? GetNextJob()
        {
            if (Status == null)
            {
                return RequiresHashingBeforeUpload ? PackageJob.Hashing : PackageJob.Upload;
            }

            switch (Status?.Status)
            {
                case JobStatus.Cancelled:
                case JobStatus.Failed:
                case JobStatus.Paused:
                    return Status.Job;

                case JobStatus.Running:
                case JobStatus.Queued:
                    return null;
            }

            if (Status?.Job == PackageJob.Hashing && !IsUploadFinished)
            {
                return PackageJob.Upload;
            }
            else if (Status?.Job == PackageJob.Upload && RequiresHashingAfterUpload)
            {
                return PackageJob.Hashing;
            }

            return null;
        }

        protected override Task StartAsync(PackageJob packageJob, PauseToken pauseToken = default, CancellationToken cancellationToken = default)
        {
            switch (packageJob)
            {
                case PackageJob.Hashing:
                    return StartHashingAsync(pauseToken, cancellationToken);

                case PackageJob.Upload:
                    return StartUploadAsync(pauseToken, cancellationToken);
            }

            return Task.CompletedTask;
        }

        private Task StartUploadAsync(PauseToken pauseToken = default, CancellationToken cancellationToken = default)
        {
            if (!FileInfo.Exists)
            {
                ChangeStatus(PackageJob.Upload, $"File '{FileInfo.FullName}' not found");

                return Task.CompletedTask;
            }

            ResetProgressValues();

            string? username = FileHosterLogin?.Username;
            string? password = FileHosterLogin?.Password;
            return !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
                ? FileHoster.UploadAsync(FileInfo.FullName, username, password, cancellationToken)
                : FileHoster.UploadAsync(FileInfo.FullName, cancellationToken);
        }

        private Task StartHashingAsync(PauseToken pauseToken = default, CancellationToken cancellation = default)
        {
            if (!FileInfo.Exists)
            {
                ChangeStatus(PackageJob.Hashing, $"File '{FileInfo.FullName}' not found");

                return Task.CompletedTask;
            }

            ResetProgressValues();

            return FileHoster.HashAsync(FileInfo.FullName, pauseToken, cancellation);
        }

        private void ResetProgressValues()
        {
            Error = null;
            BytesRemaining = Size;
            StartedDate = DateTime.Now;
            FinishedDate = null;
            Duration = null;
            Speed = null;
            TimeRemaining = null;
            BytesLoaded = null;
            Progress = null;
        }

        private void FileHoster_UploadProgress(object? sender, FileHosterUploadProgressEventArgs e)
        {
            Duration = DateTime.Now - e.DateTimeStarted;
            BytesRemaining = e.BytesRemaining;
            BytesLoaded = e.BytesProcessed;
            Progress = e.Progress;
            Speed = e.Speed;
            Duration = e.TimeElapsed;
            TimeRemaining = e.TimeRemaining;
        }

        private void FileHoster_UploadFinished(object? sender, FileHosterUploadFinishedEventArgs e)
        {
            IsUploadFinished = true;

            Duration = e.TimeElapsed;
            if (e.Success)
            {
                BytesRemaining = null;
                FileUrl = e.FileInfo?.Url;
                Progress = 100.0;
            }
            else
            {
                Error = e.Result;
            }

            Speed = null;
            TimeRemaining = null;
            FinishedDate = e.DateTimeFinished;

            ChangeStatus(PackageJob.Upload, e.Success ? JobStatus.Success : JobStatus.Failed);
        }

        private void FileHoster_HashingProgress(object? sender, FileHosterHashingProgressEventArgs e)
        {
            Duration = DateTime.Now - e.DateTimeStarted;
            BytesRemaining = e.BytesRemaining;
            BytesLoaded = e.BytesProcessed;
            Progress = e.Progress;
            Speed = e.Speed;
            Duration = e.TimeElapsed;
            TimeRemaining = e.TimeRemaining;
        }

        private void FileHoster_HashingFinished(object? sender, FileHosterHashingFinshedEventArgs e)
        {
            Duration = e.TimeElapsed;

            if (e.Success)
            {
                Progress = 100.0;
                BytesRemaining = IsUploadFinished ? null : Size;
            }
            else
            {
                Error = e.Error;
            }

            Speed = null;
            TimeRemaining = null;

            ChangeStatus(PackageJob.Hashing, e.Success ? JobStatus.Success : JobStatus.Failed);
        }
    }
}
