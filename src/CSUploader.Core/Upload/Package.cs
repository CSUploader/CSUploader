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
        NotifyOwnDisplayPropertiesChanged();

        PackageFile[] snapshot;
        lock (_filesLock)
        { snapshot = [.. PackageFiles]; }
        foreach (PackageFile file in snapshot)
        {
            file.NotifyDisplayPropertiesChanged();
        }
    }

    /// <summary>
    /// Raises PropertyChanged for this package's OWN aggregated display properties, without cascading to
    /// its files. <see cref="NotifyChangedRows"/> uses this so an expanded file is notified once (as its
    /// own row) rather than twice (also via the cascade).
    /// </summary>
    private void NotifyOwnDisplayPropertiesChanged()
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
    }

    /// <summary>
    /// Per-tick UI refresh for one package. In a single locked pass, finds the files that are actively
    /// transferring (<see cref="FileState.Uploading"/> / <see cref="FileState.Hashing"/> — whose progress
    /// changes every tick) or have changed <see cref="PackageFile.State"/> since the last refresh, and
    /// raises PropertyChanged for only those files plus this package. A package with nothing running or
    /// transitioned is skipped entirely — no notifications, no allocation — which is what keeps a 500+ file
    /// queue responsive while ~20 upload: the old blanket re-notify raised ~16 events for EVERY row every
    /// tick regardless of whether it changed. Files are notified whether or not they are currently visible
    /// (a collapsed/filtered row has no bound cells, so its notify is a cheap no-op).
    /// </summary>
    public void NotifyChangedRows()
    {
        List<PackageFile>? changed = null;
        lock (_filesLock)
        {
            foreach (PackageFile file in PackageFiles)
            {
                bool active = file.State is FileState.Uploading or FileState.Hashing;
                bool stateChanged = file.State != file.LastNotifiedState;
                if (active || stateChanged)
                {
                    (changed ??= []).Add(file);
                    file.LastNotifiedState = file.State;
                }
            }
        }

        if (changed is null)
        {
            return;
        }

        NotifyOwnDisplayPropertiesChanged();
        foreach (PackageFile file in changed)
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
            // ETA for the WHOLE package (queued files included), computed the same way as the Upload
            // Overview bar: total remaining bytes / current aggregate speed. The previous version summed
            // the remaining bytes of only the currently-Uploading/Hashing files, so it reported the time
            // to finish the active batch — far shorter than reality while files are still queued.
            long? remaining = BytesRemaining;
            long? speed = Speed;
            return remaining is > 0 && speed is > 0
                ? TimeSpan.FromSeconds(remaining.Value / (double)speed.Value)
                : null;
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

            if (files.Length == 0)
            {
                return null;
            }

            // Byte-weighted over the WHOLE package (bytes uploaded / total size). A per-file average
            // (a) weighted a tiny finished file the same as a huge queued one and (b) silently dropped
            // queued files — LINQ Average over double? skips nulls, and a queued file's per-file Progress
            // is null — so the package % was the mean of only the STARTED files, wildly inflating it while
            // large files were still queued. Byte-weighting matches the Bytes Loaded / Size columns and
            // the overview. (Completed files set BytesLoaded = Size, so they contribute their full size.)
            long totalSize = files.Sum(pf => pf.Size ?? 0);
            if (totalSize <= 0)
            {
                // Sizes unknown — fall back to the per-file average of whatever progress exists.
                return files.DefaultIfEmpty().Average(u => u?.Progress);
            }

            long loaded = files.Sum(pf => pf.BytesLoaded ?? 0);
            return (double)loaded / totalSize * 100.0;
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
    /// Computes every footer aggregate this package contributes in ONE locked pass over its files,
    /// instead of the ~9 separate aggregate getters above (each of which took the lock and copied the
    /// whole file list). <c>UploadsViewModel</c>'s per-tick summary sums these across packages, so the
    /// Upload Overview does one pass per package rather than ~14. Each numeric field mirrors the
    /// corresponding footer "<c>?? 0</c>" sum; <see cref="PackageAggregate.Speed"/> is gated to actively
    /// hashing/uploading files exactly as the <see cref="Speed"/> property is, so the total matches
    /// <c>Packages.Sum(p =&gt; p.Speed ?? 0)</c>.
    /// </summary>
    public PackageAggregate ComputeAggregate()
    {
        int count = 0, uploading = 0, completed = 0, cancelled = 0, failed = 0;
        long size = 0, loaded = 0, remaining = 0, speed = 0;
        bool anyActiveSpeed = false;
        DateTime? oldestActiveStart = null;

        lock (_filesLock)
        {
            foreach (PackageFile f in PackageFiles)
            {
                count++;
                if (f.Size is long sz)
                {
                    size += sz;
                }

                if (f.BytesLoaded is long bl)
                {
                    loaded += bl;
                }

                if (f.BytesRemaining is long br)
                {
                    remaining += br;
                }

                if (f.Speed is long sp)
                {
                    speed += sp;
                }

                FileState state = f.State;
                if (state is FileState.Hashing or FileState.Uploading)
                {
                    if (f.Speed.HasValue)
                    {
                        anyActiveSpeed = true;
                    }

                    // Earliest start among the still-active files — seeds the Overview's Elapsed clock
                    // when a run is discovered already in flight (e.g. it began while the tab was hidden).
                    if (f.StartedDate is { } started && (oldestActiveStart is null || started < oldestActiveStart))
                    {
                        oldestActiveStart = started;
                    }
                }

                switch (state)
                {
                    case FileState.Uploading:
                        uploading++;
                        break;
                    case FileState.Completed:
                        completed++;
                        break;
                    case FileState.Cancelled:
                        cancelled++;
                        break;
                    case FileState.Failed:
                        failed++;
                        break;
                }
            }
        }

        return new PackageAggregate(count, size, loaded, remaining, anyActiveSpeed ? speed : 0, uploading, completed, cancelled, failed, oldestActiveStart);
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
                ? Lib.Localization.Localizer.Instance["Wizard_Step2_AccountAnonymous"]
                : login.DisplayName;
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
    /// Gets the earliest StartedDate across child files that have started, or null if none has.
    /// Mirrors <see cref="PackageFile.StartedDate"/> so the Uploads grid's "Started" column — which
    /// binds StartedDate for both package and file rows — resolves on package rows instead of logging
    /// a missing-accessor binding error; a package "starts" when its first file does.
    /// </summary>
    public DateTime? StartedDate
    {
        get
        {
            PackageFile[] files;
            lock (_filesLock)
            { files = [.. PackageFiles]; }
            DateTime[] started = [.. files.Where(f => f.StartedDate is not null).Select(f => f.StartedDate!.Value)];
            return started.Length == 0 ? null : started.Min();
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

                // Per-hoster allow-list (the upload wizard's Summary-page fit): when an entry exists
                // for this hoster, only its listed files go to it. No entry / null map → unrestricted
                // (the default cross-product). This only RESTRICTS — the size + quota filters below
                // still apply on top.
                if (Options.IncludedFilesPerHoster is { } includedPerHoster
                    && includedPerHoster.TryGetValue(fileHoster.Name, out HashSet<string>? includedFiles)
                    && !includedFiles.Contains(filePath))
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
            .Select(fh => FileHosterClient.FindByHost(fh.Key, kvp.Key.Protocol, Options.Logger ?? Logger.Current))
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

/// <summary>
/// Immutable per-package rollup produced by <see cref="Package.ComputeAggregate"/>: the file count,
/// byte totals, active-upload speed, and terminal-state counts, all summed in a single pass. The Uploads
/// footer adds these across packages instead of re-scanning every file once per displayed field.
/// </summary>
/// <param name="FileCount">Number of files in the package.</param>
/// <param name="TotalBytes">Sum of file sizes (0 when none are known) — matches <c>Package.Size ?? 0</c>.</param>
/// <param name="BytesLoaded">Sum of bytes uploaded — matches <c>Package.BytesLoaded ?? 0</c>.</param>
/// <param name="BytesRemaining">Sum of bytes remaining — matches <c>Package.BytesRemaining ?? 0</c>.</param>
/// <param name="Speed">Aggregate speed in bytes/sec, 0 unless a file is actively hashing/uploading —
/// matches <c>Package.Speed ?? 0</c>.</param>
/// <param name="Uploading">Files in <see cref="FileState.Uploading"/>.</param>
/// <param name="Completed">Files in <see cref="FileState.Completed"/>.</param>
/// <param name="Cancelled">Files in <see cref="FileState.Cancelled"/>.</param>
/// <param name="Failed">Files in <see cref="FileState.Failed"/>.</param>
/// <param name="OldestActiveStart">Earliest <see cref="PackageFile.StartedDate"/> among files currently
/// hashing/uploading, or null when none are active — seeds the Overview's Elapsed clock when a run is
/// first observed already in flight.</param>
public readonly record struct PackageAggregate(
    int FileCount,
    long TotalBytes,
    long BytesLoaded,
    long BytesRemaining,
    long Speed,
    int Uploading,
    int Completed,
    int Cancelled,
    int Failed,
    DateTime? OldestActiveStart = null);
