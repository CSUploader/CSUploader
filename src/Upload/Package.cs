// <copyright file="Package.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.ComponentModel;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Upload.Pipeline;
using IOPath = System.IO.Path;

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
    public string Name { get; set; } = options.Title;

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
    /// Gets the Order-column text for the package row. A package is a grouping row, not a queue
    /// entry — only individual files carry a <see cref="PackageFile.QueueOrder"/> — so a package
    /// has no position in the upload queue and the Order column is always blank for it.
    /// </summary>
#pragma warning disable CA1822 // Must be instance for {Binding OrderDisplay} to resolve via the row's DataContext.
    public string OrderDisplay => string.Empty;
#pragma warning restore CA1822

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
    /// Display name of the hoster(s). Uses the first hoster — packages always have at
    /// least one (PackageManager refuses to construct an empty package).
    /// </summary>
    public string HosterDisplay => FileHosters[0].Name;

    /// <summary>
    /// Display name of the representative account (the first login), mirroring
    /// <see cref="HosterDisplay"/>'s first-hoster approach. Anonymous shows the localized
    /// "(anonymous)" label. Read from <see cref="FileHosterLogins"/>'s values (the login DTOs),
    /// not <see cref="FileHosters"/> (the hoster clients).
    /// </summary>
    public string AccountDisplay
    {
        get
        {
            FileHosterLoginDto login = FileHosterLogins.Values.First();
            return login.IsAnonymous
                ? CSUploader.Lib.Localization.Localizer.Instance["Wizard_Step2_AccountAnonymous"]
                : (string.IsNullOrWhiteSpace(login.Username) ? string.Empty : login.Username);
        }
    }

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

            int? global = Options.Settings?.SpeedLimit;
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

            // In-progress checks come first: a single failed file shouldn't flip the
            // whole package to "Failed" while siblings are still hashing/uploading —
            // the package is only terminal once every file has reached a terminal state.
            if (states.Any(s => s == FileState.Uploading))
            {
                return FileState.Uploading;
            }

            if (states.Any(s => s == FileState.Hashing))
            {
                return FileState.Hashing;
            }

            if (states.Any(s => s is FileState.HashQueued or FileState.UploadQueued))
            {
                return FileState.UploadQueued;
            }

            if (states.Any(s => s == FileState.Paused))
            {
                return FileState.Paused;
            }

            // Past this point every file is terminal (Completed / Failed / Cancelled)
            // or Idle. Choose the rollup that best describes the outcome.
            bool anyCompleted = states.Any(s => s == FileState.Completed);
            bool anyFailed = states.Any(s => s is FileState.Failed or FileState.Cancelled);

            if (anyCompleted && anyFailed)
            {
                return FileState.CompletedWithErrors;
            }

            if (states.All(s => s == FileState.Completed))
            {
                return FileState.Completed;
            }

            if (states.Any(s => s == FileState.Failed))
            {
                return FileState.Failed;
            }

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
    /// Gets the directory all package files share, or the longest common parent if files
    /// span subfolders, or null if no files are present or files span unrelated roots
    /// (e.g. different drives). Computed live from <see cref="PackageFile.Path"/>
    /// values; not stored.
    /// </summary>
    public string? Path
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }
            if (files.Length == 0)
            {
                return null;
            }

            return LongestCommonDirectory(files.Select(f => f.Path));
        }
    }

    /// <summary>
    /// Returns the longest directory that is an ancestor of every non-null/non-empty
    /// path in <paramref name="dirs"/>. Returns null when there is no shared root
    /// (e.g. paths on different drives) or when no usable input is supplied. Public
    /// for direct unit testing.
    /// </summary>
    public static string? LongestCommonDirectory(IEnumerable<string?> dirs)
    {
        string[] arr = [.. dirs.Where(d => !string.IsNullOrEmpty(d))!];
        if (arr.Length == 0)
        {
            return null;
        }
        if (arr.Length == 1)
        {
            return arr[0];
        }

        string current = arr[0];
        for (int i = 1; i < arr.Length; i++)
        {
            current = CommonAncestor(current, arr[i]);
            if (string.IsNullOrEmpty(current))
            {
                return null;
            }
        }
        return current;
    }

    private static string CommonAncestor(string a, string b)
    {
        string? candidate = a;
        while (!string.IsNullOrEmpty(candidate))
        {
            if (string.Equals(candidate, b, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            string withSep = candidate.EndsWith(IOPath.DirectorySeparatorChar)
                ? candidate
                : candidate + IOPath.DirectorySeparatorChar;
            if (b.StartsWith(withSep, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            candidate = IOPath.GetDirectoryName(candidate);
        }
        return string.Empty;
    }

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
    /// Adds one <see cref="PackageFile"/> per (selected file × configured hoster)
    /// using paths from <see cref="PackageOptions.SelectedFiles"/>. When
    /// <paramref name="registry"/> is non-null, (file, hoster) pairs whose file
    /// exceeds the registered pipeline's per-file cap for that account
    /// (<c>MaxFileSizeFor</c>, which can vary by tier — e.g. Hexload anonymous) are skipped —
    /// otherwise they'd be queued, fail at the pipeline's pre-check at attempt time,
    /// and clutter the Uploads grid with rows that never had a chance. Pairs with no
    /// registered pipeline, no declared cap, or a file whose on-disk size can't be
    /// read are kept (let the runtime decide).
    /// </summary>
    public void AddPackageFiles(IFileHosterRegistry? registry = null, IAppLogger? logger = null)
    {
        if (Options.SelectedFiles is not { Count: > 0 } selected)
        {
            return;
        }

        List<PackageFile> packageFiles = [];
        foreach (string filePath in selected)
        {
            long fileSize = SafeGetFileSize(filePath);
            foreach (KeyValuePair<FileHosterClient, FileHosterLoginDto> kvp in FileHosterLogins)
            {
                FileHosterClient? fileHoster = ResolveHosterClient(kvp);
                if (fileHoster is null)
                {
                    continue;
                }

                if (registry?.Find(fileHoster.Name) is IFileHosterPipeline pipeline
                    && pipeline.MaxFileSizeFor(kvp.Value) is long cap
                    && fileSize > 0
                    && fileSize > cap)
                {
                    logger?.Log(
                        this,
                        LogType.Status,
                        $"Skipping queueing of '{IOPath.GetFileName(filePath)}' on {fileHoster.Name}: "
                        + $"{ByteUnit.FromBytes(fileSize, ByteBase.Binary).ToFriendlyString()} exceeds the "
                        + $"{ByteUnit.FromBytes(cap, ByteBase.Binary).ToFriendlyString()} per-file limit.");
                    continue;
                }

                // Storage-quota filter: skip when the file would push the account past
                // its known quota. Persisted on the DTO by pipelines whose API exposes
                // usage (FileBoom). Hosters that don't surface quota leave the fields
                // null and we don't apply this filter.
                if (kvp.Value.StorageQuotaBytes is long quota
                    && kvp.Value.StorageUsedBytes is long used
                    && fileSize > 0
                    && used + fileSize > quota)
                {
                    long remaining = Math.Max(0, quota - used);
                    logger?.Log(
                        this,
                        LogType.Status,
                        $"Skipping queueing of '{IOPath.GetFileName(filePath)}' on {fileHoster.Name}: "
                        + $"{ByteUnit.FromBytes(fileSize, ByteBase.Binary).ToFriendlyString()} would exceed the account's "
                        + $"{ByteUnit.FromBytes(remaining, ByteBase.Binary).ToFriendlyString()} of remaining "
                        + $"{ByteUnit.FromBytes(quota, ByteBase.Binary).ToFriendlyString()} storage quota.");
                    continue;
                }

                packageFiles.Add(new PackageFile(this, filePath, fileHoster, kvp.Value));
            }
        }

        AddPackageFiles([.. packageFiles]);
    }

    private static long SafeGetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            // File missing / inaccessible. Return a sentinel so the caller doesn't apply
            // the size filter and the runtime gets to surface the real "file not found"
            // error per its normal path instead of being silently dropped here.
            return -1;
        }
    }

    private FileHosterClient? ResolveHosterClient(KeyValuePair<FileHosterClient, FileHosterLoginDto> kvp)
    {
        return FileHosterClient.FileHosters
            .Where(fh => fh.Key == kvp.Key.Name)
            .Select(fh => FileHosterClient.FindByHost(fh.Key, kvp.Key.Protocol, Options.Logger ?? Lib.Logger.Current))
            .FirstOrDefault();
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
