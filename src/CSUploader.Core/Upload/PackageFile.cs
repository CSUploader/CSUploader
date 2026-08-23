// <copyright file="PackageFile.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using CSUploader.Dal;
using CSUploader.Lib;
using IOPath = System.IO.Path;

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
        Name = IOPath.GetFileName(filePath);
        FileInfo = new FileInfo(filePath);

        FileHoster = fileHoster;
        FileHosterLogin = fileHosterLoginDto;
        Path = IOPath.GetDirectoryName(filePath);
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
    /// than the credential's name (Username is null on reloaded anonymous packages — see
    /// PackageManager reconstitution — so the IsAnonymous guard must stay in front). Real accounts
    /// use <see cref="FileHosterLoginDto.DisplayName"/>, which falls back to a masked API key for
    /// key-only hosters (Ufile/NitroFlare) that have no username. Fixed at construction like
    /// HosterDisplay, so no change notification is needed.
    /// </summary>
    public string AccountDisplay => FileHosterLogin.IsAnonymous
        ? Lib.Localization.Localizer.Instance["Wizard_Step2_AccountAnonymous"]
        : FileHosterLogin.DisplayName;

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
    /// The <see cref="State"/> value the UI-refresh pass last raised change notifications for. Used by
    /// <see cref="Package.NotifyChangedRows"/> to detect a transition and refresh this row exactly once
    /// on it — <see cref="State"/> has a plain setter (no PropertyChanged), so before the per-row refresh
    /// only re-notified running/changed rows, the 500 ms tick's blanket re-notify was what surfaced
    /// transitions. Written only on the UI thread by that pass.
    /// </summary>
    internal FileState LastNotifiedState { get; set; } = FileState.Idle;

    /// <summary>
    /// Gets or sets the cancellation token source owned by the scheduler.
    /// </summary>
    public CancellationTokenSource? Cts { get; set; }

    /// <summary>
    /// Gets or sets which attempt currently owns this row. Bumped by the scheduler each time it
    /// launches a hash or an upload for this file.
    /// </summary>
    /// <remarks>
    /// A worker's completion callback is queued on the scheduler's pump and runs some time after
    /// the work itself ends. If the user stops the file and starts it again in that window, the OLD
    /// callback arrives to find a NEW attempt in possession of <see cref="Cts"/> and
    /// <see cref="State"/>. Acting on it disposed the new attempt's source without cancelling it —
    /// leaving an upload nothing could stop, behind a row that claimed to be cancelled. The
    /// scheduler stamps each worker with the generation it launched under and drops callbacks that
    /// no longer match. Read and written only on the pump thread.
    /// </remarks>
    internal int AttemptGeneration { get; set; }

    /// <summary>
    /// Retires the attempt currently owning this row, so its completion callback is ignored when it
    /// eventually arrives.
    /// </summary>
    /// <remarks>
    /// Cancelling a worker does not stop its callback: the work may already have FINISHED, with its
    /// completion sitting in the scheduler's queue behind the stop. That callback reports success
    /// and, for a hash, moves the row to UploadQueued — starting the upload of a file the user just
    /// stopped. Reset has the mirror problem: it queues the file again, and the cancelled attempt's
    /// callback then paints Cancelled back over it, silently undoing the reset.
    /// <para>
    /// Every operation that SUPERSEDES an attempt calls this — stop, reset, detach. Pause must not:
    /// it cancels the work but leaves the row Uploading precisely so the callback can park it.
    /// </para>
    /// </remarks>
    internal void SupersedeAttempt() => AttemptGeneration++;

    /// <summary>
    /// Gets or sets a value indicating whether a user action has just thrown this file's hash away,
    /// so the stored one must go with it.
    /// </summary>
    /// <remarks>
    /// Reset and the re-upload of a Completed file both clear <see cref="FileHash"/> and
    /// <see cref="IsHashingComplete"/> in memory. Nothing in the persistence path clears a stored
    /// hash — it only ever writes one — so without this the row kept the old hash and its flag, and
    /// a restart before the file got a slot reloaded that hash and skipped straight to uploading:
    /// exactly what re-hashing exists to prevent. Set on the scheduler's pump immediately before
    /// the state transition that announces the change; <c>PackageManager.OnFileStateChanged</c>
    /// reads and clears it inline on that same thread.
    /// </remarks>
    internal bool HashDiscarded { get; set; }

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
    /// Gets or sets the local source directory the file was added from (the directory
    /// portion of its original path on disk).
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Per-file speed limit override in KB/s. Null means fall back to the package's limit.
    /// </summary>
    public int? SpeedLimitKBps { get; set; }

    /// <summary>
    /// The bucket in force for this file: its own override when set, else its package's (which is
    /// in turn the package's own or the global). Mirrors the resolution order of
    /// <see cref="GetEffectiveSpeedLimitBytesPerSecond"/> — only the SCOPE of enforcement is new.
    /// </summary>
    public Lib.Net.Http.SpeedLimiter SpeedLimiter
        => SpeedLimitKBps is > 0 ? SpeedLimitScopes.ForFile(this) : Package.SpeedLimiter;

    /// <summary>What this file's streams hand to ThrottledStream. Resolved per read, never cached,
    /// because the user can change a limit mid-upload.</summary>
    public Lib.Net.Http.SpeedBudget SpeedBudget => new(() => SpeedLimiter);

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
                // A completed file is fully on the server. Normal uploads have already driven
                // BytesLoaded to the full size via TransferProgress, but some hosters short-circuit
                // with no byte transfer (e.g. Alfafile returns the link instantly when it already
                // has the file by hash) — so set BytesLoaded to the size to match the 100% progress
                // instead of leaving a misleading 0.
                BytesLoaded = Size;
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
        SpeedBudget = SpeedBudget,
        MaxParallelPartsCeiling = Package.Options.Settings?.MaxParallelPartsPerFile
            ?? AppSettings.DefaultMaxParallelPartsPerFile,
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

    /// <summary>
    /// Clears the PREVIOUS attempt's display values when the file is re-queued (Start/Retry of an
    /// Idle/Paused/Failed/Cancelled row — including one restored from an earlier session, whose
    /// Duration/dates the loader rebuilds from the persisted attempt). A queued file must read like a
    /// queued file: no stale Elapsed/Started/Finished/progress from the attempt that failed. Distinct
    /// from <see cref="ResetProgressValues"/>, which runs when the NEW attempt actually starts (and
    /// stamps <see cref="StartedDate"/> = now); this one leaves the row blank while it waits. Error is
    /// intentionally not touched — the two re-queue call sites keep their existing clearing rules.
    /// </summary>
    internal void ResetAttemptDisplay()
    {
        StartedDate = null;
        FinishedDate = null;
        Duration = null;
        Speed = null;
        TimeRemaining = null;
        BytesLoaded = null;
        Progress = null;
        BytesRemaining = Size;
    }
}
