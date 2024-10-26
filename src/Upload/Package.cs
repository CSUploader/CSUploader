// <copyright file="Package.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Compression;

namespace CSUploader.Upload
{
    /// <summary>
    /// A Package.
    /// </summary>
    /// <seealso cref="CSUploader.Models.PackageDetails" />
    /// <seealso cref="IEnumerable{CSUploader.Models.PackageFile}" />
    public class Package : PackageDetails, IEnumerable<PackageFile>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Package"/> class.
        /// </summary>
        /// <param name="options">The options.</param>
        public Package(PackageOptions options)
        {
            Options = options;

            Name = Path.GetFileNameWithoutExtension(options.DirectoryPath) ?? throw new ArgumentException(nameof(options.DirectoryPath));
            SaveFrom = options.DirectoryPath;
            FileHosterLogins = options.FileHosters;

            if (Compressor != null)
            {
                Compressor.StatusChanged += Compressor_StatusChanged;
            }
        }

        /// <summary>
        /// Event triggered when a package files are added to the package.
        /// </summary>
        public event EventHandler<PackageAddedEventArgs>? PackageFilesAdded;

        /// <summary>
        /// Gets the total size of the archive package.
        /// </summary>
        public override long? Size => Compressor != null && IsCompressing ? Compressor.Size : (PackageFiles.Any(s => s.Size.HasValue) ? PackageFiles.Sum(u => u.Size) : null);

        /// <summary>
        /// Gets the file hosters used the package is uploading to.
        /// </summary>
        public override FileHosterClient[] FileHosters => FileHosterLogins.Select(fh => fh.Key).ToArray();

        /// <summary>
        /// Gets the bytes left of package to upload.
        /// </summary>
        public override long? BytesRemaining => Compressor != null && IsCompressing ? Compressor.BytesRemaining : (PackageFiles.Any(pf => pf.BytesRemaining.HasValue) ? PackageFiles.Sum(pf => pf.BytesRemaining) : null);

        /// <summary>
        /// Gets the duration the file is uploading (when uploading; pause/stopped/etc. time is not included).
        /// </summary>
        public override TimeSpan? Duration => IsCompressing ? DateTime.Now - AddedDate : PackageFiles.Select(pf => pf.Duration).DefaultIfEmpty().Aggregate((result, ts) => result.HasValue && ts.HasValue ? result.Value.Add(ts.Value) : ts ?? result);

        /// <summary>
        /// Gets the upload, compression or hashing speed.
        /// </summary>
        public override long? Speed => Compressor != null && IsCompressing ? Compressor.Speed : PackageFiles.Any(pf => pf.Status?.Status == JobStatus.Running && pf.Speed.HasValue) ? PackageFiles.Sum(p => p.Speed) : null;

        /// <summary>
        /// Gets the ETA until the job is complete.
        /// </summary>
        public override TimeSpan? TimeRemaining
        {
            get
            {
                if (Compressor != null && IsCompressing)
                {
                    return Compressor.TimeRemaining;
                }

                bool haveTime = false;
                double timeRemaining = 0.0;
                foreach (IGrouping<PackageJob, PackageFile> files in PackageFiles.Where(pf => pf.Status != null).GroupBy(pf => pf.Status.Job))
                {
                    if (!files.Any(pf => pf.Status?.Status == JobStatus.Running))
                    {
                        continue;
                    }

                    long jobTotalBytesRemaining = 0;
                    long jobTotalBytesLoaded = 0;
                    double jobTotalTimeElapsed = 0.0;
                    foreach (PackageFile packageFile in files)
                    {
                        if (packageFile.BytesRemaining.HasValue)
                        {
                            jobTotalBytesRemaining += packageFile.BytesRemaining.Value;
                        }

                        if (packageFile.BytesLoaded.HasValue)
                        {
                            jobTotalBytesLoaded += packageFile.BytesLoaded.Value;
                        }

                        if (packageFile.Duration.HasValue)
                        {
                            jobTotalTimeElapsed += packageFile.Duration.Value.TotalSeconds;
                        }
                    }

                    if (jobTotalBytesLoaded > 0 && jobTotalBytesRemaining > 0)
                    {
                        haveTime = true;
                        timeRemaining += TimeSpan.FromSeconds(jobTotalTimeElapsed / jobTotalBytesLoaded * jobTotalBytesRemaining).TotalSeconds;
                    }
                }

                return haveTime ? TimeSpan.FromSeconds(timeRemaining) : (TimeSpan?)null;
            }
        }

        /// <summary>
        /// Gets the bytes uploaded or compressed.
        /// </summary>
        public override long? BytesLoaded => Compressor != null && IsCompressing ? Compressor.BytesCompressed : (PackageFiles.Any(pf => pf.BytesLoaded.HasValue) ? PackageFiles.Sum(pf => pf.BytesLoaded) : null);

        /// <summary>
        /// Gets the progress left (in %).
        /// </summary>
        public override double? Progress => Compressor != null && IsCompressing ? Compressor.Progress : PackageFiles.DefaultIfEmpty().Average(u => u?.Progress);

        /// <summary>
        /// Gets the password of the package.
        /// </summary>
        public override string? Password => Options?.CompressionOptions?.ArchivePassword;

        /// <summary>
        /// Gets the file count of the package.
        /// </summary>
        public int? FileCount => IsCompressing ? null : (PackageFiles.Any() ? PackageFiles.Count : null);

        /// <summary>
        /// Gets or sets the file hoster logins.
        /// </summary>
        /// <value>
        /// The file hoster logins.
        /// </value>
        public Dictionary<FileHosterClient, FileHosterLoginDto> FileHosterLogins { get; set; }

        /// <summary>
        /// Gets a value indicating whether the package requires compression.
        /// </summary>
        /// <value>
        ///   <c>true</c> if the package requires compression; otherwise, <c>false</c>.
        /// </value>
        public bool RequiresCompression => Compressor != null && Compressor.Status != JobStatus.Success;

        /// <summary>
        /// Gets a value indicating whether this instance is compressing.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance is compressing; otherwise, <c>false</c>.
        /// </value>
        public bool IsCompressing => Compressor?.Status == JobStatus.Running;

        public PackageOptions Options { get; private set; }

        /// <summary>
        /// Gets the compressor used for this package, if set.
        /// </summary>
        private Compressor? Compressor => Options.CompressionOptions.Compressor;

        private List<PackageFile> PackageFiles { get; set; } = new List<PackageFile>();

        public override PackageJob? GetNextJob()
        {
            if (RequiresCompression)
            {
                if (Status == null)
                {
                    return PackageJob.Compression;
                }

                switch (Status?.Status)
                {
                    case JobStatus.Cancelled:
                    case JobStatus.Failed:
                    case JobStatus.Paused:
                        return Status.Job;
                }
            }

            return null;
        }

        public void Remove(PackageFile packageFile)
        {
            // Remove it from the list first thing
            PackageFiles.Remove(packageFile);

            // Stop package file
            packageFile.Stop();
        }

        public void Remove()
        {
            if (Status?.Status == JobStatus.Running)
            {
                Stop();
            }
            else
            {
                PackageFile[] packageFiles = PackageFiles.ToArray();
                PackageFiles.Clear();

                foreach (PackageFile packageFile in packageFiles)
                {
                    packageFile.Stop();
                }
            }
        }

        public void AddPackageFiles(string directory)
        {
            List<PackageFile> packageFiles = new();

            foreach (string filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                // Upload it to each given file hoster
                foreach (KeyValuePair<FileHosterClient, FileHosterLoginDto> kvp in FileHosterLogins)
                {
                    FileHosterClient? fileHoster = FileHosterClient.FileHosters.Where(fh => fh.Key == kvp.Key.Name).Select(fh => FileHosterClient.FindByHost(fh.Key, kvp.Key.Protocol)).FirstOrDefault();
                    if (fileHoster != null)
                    {
                        PackageFile packageFile = new(this, filePath, fileHoster, kvp.Value);
                        packageFiles.Add(packageFile);
                    }
                }
            }

            AddPackageFiles(packageFiles.ToArray());
        }

        public void AddPackageFiles(PackageFile[] packageFiles)
        {
            // Add event handlers
            foreach (PackageFile packageFile in packageFiles)
            {
                packageFile.StatusChanged += PackageFile_StatusChanged;

                PackageFiles.Add(packageFile);
            }

            PackageFilesAdded?.Invoke(this, new PackageAddedEventArgs(this, packageFiles));
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>
        /// An enumerator that can be used to iterate through the collection.
        /// </returns>
        public IEnumerator<PackageFile> GetEnumerator()
        {
            return PackageFiles.GetEnumerator();
        }

        /// <summary>
        /// Returns an enumerator that iterates through a collection.
        /// </summary>
        /// <returns>
        /// An <see cref="T:System.Collections.IEnumerator" /> object that can be used to iterate through the collection.
        /// </returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return PackageFiles.GetEnumerator();
        }

        /// <summary>
        /// Starts the asynchronous.
        /// </summary>
        /// <param name="packageJob">The package job.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="pauseToken">The pause token.</param>
        /// <returns>The <see cref="Task" /> representing the asynchronous operation.</returns>
        protected override Task StartAsync(PackageJob packageJob, PauseToken pauseToken = default, CancellationToken cancellationToken = default)
        {
            if (packageJob != PackageJob.Compression || Compressor == null || string.IsNullOrEmpty(Options.DirectoryPath) || string.IsNullOrEmpty(Options.CompressionOptions.OutputDirectoryPath))
            {
                return Task.CompletedTask;
            }

            if (Options.CompressionOptions == null || Compressor.Status == JobStatus.Running || Compressor.Status == JobStatus.Success)
            {
                return Task.CompletedTask;
            }

            return Compressor.CompressAsync(Options.DirectoryPath, Options.CompressionOptions.OutputDirectoryPath, pauseToken, cancellationToken);
        }

        private void PackageFile_StatusChanged(object? sender, PackageStatusChangedEventArgs e)
        {
            FireStatusChanged(sender, e);
        }

        private void Compressor_StatusChanged(object? sender, CompressorStatusChangedEventArgs e)
        {
            ChangeStatus(PackageJob.Compression, e.NewStatus);
        }
    }
}
