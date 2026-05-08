// <copyright file="Package.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.ComponentModel;
using CSUploader.Dal;

namespace CSUploader.Upload;

/// <summary>
/// A Package. Container for <see cref="PackageFile"/> instances with aggregated display properties.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="Package"/> class.
/// </remarks>
/// <param name="options">The options.</param>
public class Package(PackageOptions options) : IEnumerable<PackageFile>, INotifyPropertyChanged
{
    private readonly Lock _filesLock = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises PropertyChanged for all display-bound aggregated properties.
    /// Also cascades to child package files so their display properties refresh.
    /// </summary>
    public void NotifyDisplayPropertiesChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Size)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Speed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BytesLoaded)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BytesRemaining)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeRemaining)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Error)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AddedDate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FinishedDate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeedLimitKBps)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveSpeedLimitKBps)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Priority)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileUrl)));

        PackageFile[] snapshot;
        lock (_filesLock)
        { snapshot = [.. PackageFiles]; }
        foreach (PackageFile file in snapshot)
        {
            file.NotifyDisplayPropertiesChanged();
        }
    }

    /// <summary>
    /// Event triggered when package files are added to the package.
    /// </summary>
    public event EventHandler<PackageAddedEventArgs>? PackageFilesAdded;

    /// <summary>
    /// Gets or sets the database primary key, or null if not yet persisted.
    /// </summary>
    public int? DbId { get; set; }

    /// <summary>
    /// Gets or sets the scheduled start time for the package upload.
    /// </summary>
    public DateTime? ScheduledStartTime { get; set; }

    /// <summary>
    /// Gets or sets the name of the package.
    /// </summary>
    public string Name { get; set; } = !string.IsNullOrWhiteSpace(options.Title)
            ? options.Title
            : Path.GetFileNameWithoutExtension(options.DirectoryPath) ?? throw new ArgumentException(nameof(options.DirectoryPath));

    /// <summary>
    /// Gets the total size of the package.
    /// </summary>
    public long? Size
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }
            return files.Any(s => s.Size.HasValue) ? files.Sum(u => u.Size) : null;
        }
    }

    /// <summary>
    /// Gets the file hosters used the package is uploading to.
    /// </summary>
    public FileHosterClient[] FileHosters => [.. FileHosterLogins.Select(fh => fh.Key)];

    /// <summary>
    /// Gets the bytes left of package to upload.
    /// </summary>
    public long? BytesRemaining
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }
            return files.Any(pf => pf.BytesRemaining.HasValue) ? files.Sum(pf => pf.BytesRemaining) : null;
        }
    }

    /// <summary>
    /// Gets the duration the file is uploading (when uploading; pause/stopped/etc. time is not included).
    /// </summary>
    public TimeSpan? Duration
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }
            return files.Select(pf => pf.Duration).DefaultIfEmpty().Aggregate((result, ts) => result.HasValue && ts.HasValue ? result.Value.Add(ts.Value) : ts ?? result);
        }
    }

    /// <summary>
    /// Gets the upload or hashing speed.
    /// </summary>
    public long? Speed
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }
            return files.Any(pf => pf.State is FileState.Hashing or FileState.Uploading && pf.Speed.HasValue) ? files.Sum(p => p.Speed) : null;
        }
    }

    /// <summary>
    /// Gets the ETA until the job is complete.
    /// </summary>
    public TimeSpan? TimeRemaining
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }

            long totalBytesRemaining = 0;
            long totalBytesLoaded = 0;
            double totalTimeElapsed = 0.0;
            bool haveRunning = false;

            foreach (PackageFile pf in files)
            {
                if (pf.State is not (FileState.Hashing or FileState.Uploading))
                {
                    continue;
                }

                haveRunning = true;

                if (pf.BytesRemaining.HasValue)
                {
                    totalBytesRemaining += pf.BytesRemaining.Value;
                }

                if (pf.BytesLoaded.HasValue)
                {
                    totalBytesLoaded += pf.BytesLoaded.Value;
                }

                if (pf.Duration.HasValue)
                {
                    totalTimeElapsed += pf.Duration.Value.TotalSeconds;
                }
            }

            if (haveRunning && totalBytesLoaded > 0 && totalBytesRemaining > 0)
            {
                return TimeSpan.FromSeconds(totalTimeElapsed / totalBytesLoaded * totalBytesRemaining);
            }

            return null;
        }
    }

    /// <summary>
    /// Gets the bytes uploaded.
    /// </summary>
    public long? BytesLoaded
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }
            return files.Any(pf => pf.BytesLoaded.HasValue) ? files.Sum(pf => pf.BytesLoaded) : null;
        }
    }

    /// <summary>
    /// Gets the progress (in %).
    /// </summary>
    public double? Progress
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }
            return files.DefaultIfEmpty().Average(u => u?.Progress);
        }
    }

    /// <summary>
    /// Gets the file count of the package.
    /// </summary>
    public int? FileCount
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }
            return files.Length > 0 ? files.Length : null;
        }
    }

    /// <summary>
    /// Gets or sets the file hoster logins.
    /// </summary>
    public Dictionary<FileHosterClient, FileHosterLoginDto> FileHosterLogins { get; set; } = options.FileHosters;

    /// <summary>
    /// Per-package speed limit override in KB/s. Null means use the global AppSettings.SpeedLimit.
    /// </summary>
    public int? SpeedLimitKBps { get; set; }

    /// <summary>
    /// Whether the package's child rows are visible in the UI.
    /// </summary>
    public bool IsExpanded
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    } = true;

    /// <summary>
    /// Alias of <see cref="Status"/> so XAML bindings can use the same path as PackageFile.State.
    /// </summary>
    public FileState State => Status;

    /// <summary>
    /// Display name of the hoster(s). Uses the first hoster; empty when none.
    /// </summary>
    public string HosterDisplay => FileHosters.Length > 0 ? FileHosters[0].Name : string.Empty;

    /// <summary>
    /// True — marks this row as a package row for XAML template selection.
    /// </summary>
#pragma warning disable CA1822
    public bool IsPackageRow => true;
#pragma warning restore CA1822

    /// <summary>
    /// Returns the effective upload speed limit in bytes/second, preferring the per-package
    /// override over the global AppSettings value. Returns null for unlimited.
    /// </summary>
    public long? GetEffectiveSpeedLimitBytesPerSecond()
    {
        int? kbps = EffectiveSpeedLimitKBps;
        return kbps is > 0 ? (long)kbps.Value * 1024 : null;
    }

    /// <summary>
    /// Gets the effective speed limit in KB/s (override or global fallback), or null for unlimited.
    /// </summary>
    public int? EffectiveSpeedLimitKBps
    {
        get
        {
            if (SpeedLimitKBps is > 0)
            {
                return SpeedLimitKBps;
            }

            int? global = AppSettings.Current.SpeedLimit;
            return global is > 0 ? global : null;
        }
    }

    /// <summary>
    /// Gets the aggregate status derived from child package files' <see cref="FileState"/> values.
    /// </summary>
    public FileState Status
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }

            if (files.Length == 0)
            {
                return FileState.Idle;
            }

            FileState[] states = [.. files.Select(f => f.State)];

            // Terminal: all files completed
            if (states.All(s => s == FileState.Completed))
            {
                return FileState.Completed;
            }

            // Terminal: any file failed
            if (states.Any(s => s == FileState.Failed))
            {
                return FileState.Failed;
            }

            // In progress: any file actively running
            if (states.Any(s => s == FileState.Uploading))
            {
                return FileState.Uploading;
            }

            if (states.Any(s => s == FileState.Hashing))
            {
                return FileState.Hashing;
            }

            // Queued: any file waiting
            if (states.Any(s => s is FileState.HashQueued or FileState.UploadQueued))
            {
                return FileState.UploadQueued;
            }

            // Paused
            if (states.Any(s => s == FileState.Paused))
            {
                return FileState.Paused;
            }

            // Cancelled
            if (states.Any(s => s == FileState.Cancelled))
            {
                return FileState.Cancelled;
            }

            return FileState.Idle;
        }
    }

    /// <summary>
    /// Gets or sets the error string.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets the earliest AddedDate across child files, or null if none.
    /// </summary>
    public DateTime? AddedDate
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }
            return files.Length == 0 ? null : files.Min(f => f.AddedDate);
        }
    }

    /// <summary>
    /// Gets the latest FinishedDate across child files, or null if any file hasn't finished.
    /// </summary>
    public DateTime? FinishedDate
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }
            if (files.Length == 0 || files.Any(f => f.FinishedDate is null))
            {
                return null;
            }

            return files.Max(f => f.FinishedDate);
        }
    }

    /// <summary>
    /// Gets the highest priority across child files, or null if the package has no files.
    /// </summary>
    public int? Priority
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }
            return files.Length == 0 ? null : files.Max(f => f.Priority);
        }
    }

    /// <summary>
    /// Gets the newline-joined URLs of child files that have finished uploading, or empty if none.
    /// </summary>
    public string FileUrl
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }
            return string.Join(Environment.NewLine, files
                .Select(f => f.FileUrl)
                .Where(u => !string.IsNullOrEmpty(u)));
        }
    }

    /// <summary>
    /// Aggregated <see cref="PackageFile.FileHash"/> for unified DataGrid bindings; empty
    /// for package rows because the per-file rows already show the individual hashes (the
    /// XAML trigger collapses this on package rows the same way it does for the URL column).
    /// </summary>
#pragma warning disable CA1822 // Must be instance for {Binding FileHash} to resolve via the row's DataContext.
    public string FileHash => string.Empty;
#pragma warning restore CA1822

    /// <summary>
    /// Gets or sets the file path of the file on disk.
    /// </summary>
    public string? SaveFrom { get; set; } = options.DirectoryPath;

    /// <summary>
    /// Gets the options used to create this package.
    /// </summary>
    public PackageOptions Options { get; private set; } = options;

    private List<PackageFile> PackageFiles { get; set; } = [];

    /// <summary>
    /// Removes a specific package file from this package.
    /// </summary>
    /// <param name="packageFile">The file to remove.</param>
    public void Remove(PackageFile packageFile)
    {
        lock (_filesLock)
        { PackageFiles.Remove(packageFile); }
        packageFile.Cts?.Cancel();
        packageFile.Cts?.Dispose();
        packageFile.Cts = null;
    }

    /// <summary>
    /// Removes all package files from this package.
    /// </summary>
    public void Remove()
    {
        PackageFile[] snapshot;
        lock (_filesLock)
        {
            snapshot = [.. PackageFiles];
            PackageFiles.Clear();
        }

        foreach (PackageFile packageFile in snapshot)
        {
            packageFile.Cts?.Cancel();
            packageFile.Cts?.Dispose();
            packageFile.Cts = null;
        }
    }

    /// <summary>
    /// Adds package files from the given directory.
    /// </summary>
    /// <param name="directory">The directory to scan for files.</param>
    public void AddPackageFiles(string directory)
    {
        List<PackageFile> packageFiles = [];
        HashSet<string>? selectedFiles = Options.SelectedFiles is { Count: > 0 }
            ? new HashSet<string>(Options.SelectedFiles, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (string filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (selectedFiles is not null && !selectedFiles.Contains(filePath))
            {
                continue;
            }

            foreach (KeyValuePair<FileHosterClient, FileHosterLoginDto> kvp in FileHosterLogins)
            {
                FileHosterClient? fileHoster = FileHosterClient.FileHosters.Where(fh => fh.Key == kvp.Key.Name).Select(fh => FileHosterClient.FindByHost(fh.Key, kvp.Key.Protocol, Options.Logger!)).FirstOrDefault();
                if (fileHoster != null)
                {
                    packageFiles.Add(new PackageFile(this, filePath, fileHoster, kvp.Value));
                }
            }
        }

        AddPackageFiles([.. packageFiles]);
    }

    /// <summary>
    /// Adds the given package files to this package.
    /// </summary>
    /// <param name="packageFiles">The files to add.</param>
    public void AddPackageFiles(PackageFile[] packageFiles)
    {
        foreach (PackageFile packageFile in packageFiles)
        {
            lock (_filesLock)
            { PackageFiles.Add(packageFile); }
        }

        PackageFilesAdded?.Invoke(this, new PackageAddedEventArgs(this, packageFiles));
    }

    /// <inheritdoc/>
    public IEnumerator<PackageFile> GetEnumerator()
    {
        PackageFile[] snapshot;
        lock (_filesLock)
        { snapshot = [.. PackageFiles]; }
        return ((IEnumerable<PackageFile>)snapshot).GetEnumerator();
    }

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
