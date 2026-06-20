// <copyright file="PackageFile.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using CSUploader.Dal;
using CSUploader.Lib;

namespace CSUploader.Upload;

/// <summary>
/// Represents a single file within a <see cref="Package"/>.
/// </summary>
public class PackageFile : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises PropertyChanged for all display-bound properties. Called by the UI refresh timer.
    /// </summary>
    public void NotifyDisplayPropertiesChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Speed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BytesLoaded)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BytesRemaining)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartedDate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FinishedDate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeRemaining)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Error)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileUrl)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileHash)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeedLimitKBps)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveSpeedLimitKBps)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Priority)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueOrder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OrderDisplay)));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PackageFile"/> class.
    /// </summary>
    /// <param name="package">The owning package.</param>
    /// <param name="filePath">The file path on disk.</param>
    /// <param name="fileHoster">The file hoster client.</param>
    /// <param name="fileHosterLoginDto">The file hoster login credentials.</param>
    public PackageFile(Package package, string filePath, FileHosterClient fileHoster, FileHosterLoginDto fileHosterLoginDto)
    {
        Package = package;
        Name = Path.GetFileName(filePath);
        FileInfo = new FileInfo(filePath);

        FileHoster = fileHoster;
        FileHosterLogin = fileHosterLoginDto;
        SaveFrom = Path.GetDirectoryName(filePath);
        FileType = FileInfo.Extension.Length > 0 ? FileInfo.Extension[1..] : string.Empty;

        // Snapshot the size once at construction. Reading FileInfo.Length on every
        // binding tick would throw FileNotFoundException for terminal-state rows
        // whose source file has since been deleted, spamming the debugger.
        Size = FileInfo.Exists ? FileInfo.Length : null;
        BytesRemaining = Size;
    }

    /// <summary>
    /// Gets or sets the database primary key, or null if not yet persisted.
    /// </summary>
    public int? DbId { get; set; }

    /// <summary>
    /// Gets or sets the name of the file.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the total size of the file. Snapshotted at construction from
    /// <see cref="FileInfo"/>; the loader overrides it with the persisted DB value
    /// for terminal-state rows whose source file is gone.
    /// </summary>
    public long? Size { get; set; }

    /// <summary>
    /// Gets the file hosters used the package is uploading to.
    /// </summary>
    public FileHosterClient[] FileHosters => [FileHoster];

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
    /// Gets the package's <see cref="Package.ScheduledStartTime"/> so the
    /// "Scheduled at" column can render on file rows too — scheduling itself is
    /// only meaningful at the package level, but every row in the same package
    /// shares the same value.
    /// </summary>
    public DateTime? ScheduledStartTime => Package.ScheduledStartTime;

    /// <summary>
    /// Gets a value indicating whether upload has finished.
    /// </summary>
    public bool IsUploadFinished { get; internal set; }

    /// <summary>
    /// Gets the file hoster client.
    /// </summary>
    public FileHosterClient FileHoster { get; }

    /// <summary>
    /// Display name of the hoster, mirroring Package.HosterDisplay for unified XAML bindings.
    /// </summary>
    public string HosterDisplay => FileHoster.Name;

    /// <summary>
    /// Display name of the account this file is uploaded with, mirroring <see cref="HosterDisplay"/>
    /// for unified XAML bindings. Anonymous uploads show the localized "(anonymous)" label rather
    /// than the credential's <c>Username</c> (which is null on reloaded anonymous packages — see
    /// PackageManager reconstitution). Fixed at construction like HosterDisplay, so no change
    /// notification is needed.
    /// </summary>
    public string AccountDisplay => FileHosterLogin.IsAnonymous
        ? CSUploader.Lib.Localization.Localizer.Instance["Wizard_Step2_AccountAnonymous"]
        : (string.IsNullOrWhiteSpace(FileHosterLogin.Username) ? string.Empty : FileHosterLogin.Username);

    /// <summary>
    /// False — marks this row as a file row (not a package) for XAML template selection.
    /// </summary>
#pragma warning disable CA1822
    public bool IsPackageRow => false;

    /// <summary>
    /// Stub so the shared Name-column template can bind IsExpanded on both package and file rows
    /// without emitting binding path warnings. Files are never expandable — the toggle is hidden.
    /// </summary>
    public bool IsExpanded { get => false; set { } }
#pragma warning restore CA1822

    /// <summary>
    /// Gets or sets the flat file state used by the <see cref="UploadScheduler"/>.
    /// </summary>
    public FileState State { get; set; } = FileState.Idle;

    /// <summary>
    /// Gets or sets the cancellation token source owned by the scheduler.
    /// </summary>
    public CancellationTokenSource? Cts { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user force-started this file from the
    /// Uploads context menu. When true the <see cref="UploadScheduler"/> launches its upload
    /// past the UPLOAD admission gate (global + per-host), while still respecting the hashing/CPU
    /// limit: a file that needs hashing waits for a free CPU slot first, then this flag makes the
    /// upload begin immediately rather than waiting for a free upload slot. The launched file
    /// still enters the normal Hashing/Uploading state, so it
    /// is counted when admitting normal files. Cleared when the file reaches a terminal state
    /// OR when its in-flight work is cancelled/torn down (stop, reset, remove, pause) — so a
    /// hash completing in the cancellation window can't make the scheduler launch an over-limit
    /// upload for a file the user no longer wants force-started, and a later normal Start/Retry
    /// behaves normally. Set on the scheduler loop; cleared both there and by the cancellation
    /// paths that already mutate <see cref="State"/>/<see cref="Cts"/> directly.
    /// </summary>
    public bool ForceStart { get; internal set; }

    /// <summary>
    /// Gets a value indicating whether hashing has been completed for this file.
    /// </summary>
    public bool IsHashingComplete { get; internal set; }

    /// <summary>
    /// Hex-encoded hash bytes computed by the file hoster (Rapidgator → MD5). Set when
    /// the <see cref="HashingFinished"/> event reports success; persisted on the DB row
    /// so it survives restarts and shows in the Hash column. Fires PropertyChanged on
    /// assignment so the DataGrid cell refreshes without waiting for the periodic timer.
    /// </summary>
    public string? FileHash
    {
        get;
        internal set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileHash)));
        }
    }

    /// <summary>
    /// Gets or sets the error string. Stored verbatim — newlines and all — so that
    /// copy-to-clipboard surfaces the original error in its raw form. Display-side
    /// flattening is handled by <see cref="Converters.SingleLineConverter"/> on the
    /// Uploads grid; without that the DataGrid would expand the row height for every
    /// embedded newline (BRupload's HTML 500-error path is the worst offender).
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the bytes remaining of the file to upload.
    /// </summary>
    public long? BytesRemaining { get; set; }

    /// <summary>
    /// Gets or sets the date the file started (uploading, hashing, etc.).
    /// </summary>
    public DateTime? StartedDate { get; set; }

    /// <summary>
    /// Gets or sets the date the file finished uploading.
    /// </summary>
    public DateTime? FinishedDate { get; set; }

    /// <summary>
    /// Gets or sets the duration the file is uploading (when uploading; pause/stopped/etc. time is not included).
    /// </summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>
    /// Gets or sets the job speed.
    /// </summary>
    public long? Speed { get; set; }

    /// <summary>
    /// Gets or sets the time remaining until the job is complete.
    /// </summary>
    public TimeSpan? TimeRemaining { get; set; }

    /// <summary>
    /// Gets or sets the bytes uploaded.
    /// </summary>
    public long? BytesLoaded { get; set; }

    /// <summary>
    /// Gets or sets the progress (in %).
    /// </summary>
    public double? Progress { get; set; }

    /// <summary>
    /// Gets or sets the file path of the file on disk.
    /// </summary>
    public string? SaveFrom { get; set; }

    /// <summary>
    /// Per-file speed limit override in KB/s. Null means fall back to the package's limit.
    /// </summary>
    public int? SpeedLimitKBps { get; set; }

    /// <summary>
    /// Returns the effective upload speed limit in bytes/second, preferring the per-file
    /// override, then the owning package, then the global setting. Null means unlimited.
    /// </summary>
    public long? GetEffectiveSpeedLimitBytesPerSecond()
    {
        if (SpeedLimitKBps is > 0)
        {
            return (long)SpeedLimitKBps.Value * 1024;
        }

        return Package.GetEffectiveSpeedLimitBytesPerSecond();
    }

    /// <summary>
    /// Gets the effective speed limit in KB/s, cascading file → package → global. Null = unlimited.
    /// </summary>
    public int? EffectiveSpeedLimitKBps
    {
        get
        {
            if (SpeedLimitKBps is > 0)
            {
                return SpeedLimitKBps;
            }

            return Package.EffectiveSpeedLimitKBps;
        }
    }

    /// <summary>
    /// Gets or sets the date the file was added.
    /// </summary>
    public DateTime AddedDate { get; set; } = DateTime.Now;

    /// <summary>
    /// Gets the owning package's upload priority. Pass-through so the file-row's
    /// Priority column shares the package-level value (priority is a per-package
    /// concept; individual files don't carry their own).
    /// </summary>
    public PackagePriority Priority => Package.Priority;

    /// <summary>
    /// Global upload position across all packages (1-based; lower uploads sooner). The
    /// scheduler orders every file by this value. Maintained dense (1..N) over non-terminal
    /// files; terminal files keep a stale value that is not displayed. Fires PropertyChanged
    /// so the Order cell refreshes immediately on a reorder.
    /// </summary>
    public int QueueOrder
    {
        get;
        internal set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueOrder)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OrderDisplay)));
        }
    }

    /// <summary>Order-column text: blank for terminal/unplaced files, else the 1-based QueueOrder.</summary>
    public string OrderDisplay => State is FileState.Completed or FileState.Failed or FileState.Cancelled || QueueOrder <= 0
        ? string.Empty
        : QueueOrder.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>
    /// Raises <see cref="PropertyChanged"/> for the given property name. Allows
    /// <see cref="Upload.Package"/> to cascade aggregated-property changes into
    /// child file rows (e.g. Priority) without exposing the event handler list.
    /// </summary>
    internal void RaisePropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Consumes a single <see cref="Pipeline.UploadEvent"/> emitted by <see cref="Pipeline.AttemptRunner"/>.
    /// Replaces the four-event subscription pattern (UploadProgress / UploadFinished /
    /// HashingProgress / HashingFinished) — those events stay during the migration window
    /// for hashing, but the upload portion now flows through here.
    /// </summary>
    public void ApplyEvent(Pipeline.UploadEvent ev)
    {
        switch (ev)
        {
            case Pipeline.TransferStarted ts:
                ResetProgressValues();
                BytesRemaining = ts.TotalBytes;
                BytesLoaded = 0;
                Progress = 0.0;
                StartedDate = DateTime.Now;
                break;

            case Pipeline.TransferProgress tp:
                BytesLoaded = tp.BytesUploaded;
                long remaining = tp.TotalBytes - tp.BytesUploaded;
                BytesRemaining = remaining;
                Progress = tp.PercentComplete;
                Speed = (long)tp.SpeedBytesPerSec;
                Duration = StartedDate.HasValue ? DateTime.Now - StartedDate.Value : null;
                TimeRemaining = tp.SpeedBytesPerSec > 0 && remaining > 0
                    ? TimeSpan.FromSeconds(remaining / tp.SpeedBytesPerSec)
                    : null;
                break;

            case Pipeline.TransferCompleted tc:
                IsUploadFinished = true;
                FileUrl = tc.FileUrl;
                Progress = 100.0;
                BytesRemaining = null;
                Speed = null;
                FinishedDate = DateTime.Now;
                Duration = StartedDate.HasValue ? FinishedDate.Value - StartedDate.Value : Duration;
                TimeRemaining = null;
                break;

            case Pipeline.AttemptFailed af:
                Error = af.Reason;
                Speed = null;
                FinishedDate = DateTime.Now;
                Duration = StartedDate.HasValue ? FinishedDate.Value - StartedDate.Value : Duration;
                TimeRemaining = null;
                break;

            case Pipeline.AttemptCancelled:
                FinishedDate = DateTime.Now;
                Speed = null;
                Duration = StartedDate.HasValue ? FinishedDate.Value - StartedDate.Value : Duration;
                TimeRemaining = null;
                break;
        }
    }

    private FileInfo FileInfo { get; set; }

    /// <summary>
    /// Builds the immutable inputs for one upload attempt. Called by <see cref="UploadScheduler"/>
    /// just before invoking <see cref="Pipeline.AttemptRunner.RunAsync"/>.
    /// </summary>
    public Pipeline.AttemptInputs BuildAttemptInputs(IAppLogger logger) => new()
    {
        FilePath = FileInfo.FullName,
        FileName = Name,
        FileSize = FileInfo.Length,
        FileHash = FileHash,
        HosterName = FileHoster.Name,
        Credentials = FileHosterLogin,
        Logger = logger,
        SpeedLimitProvider = GetEffectiveSpeedLimitBytesPerSecond,
    };

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
}
