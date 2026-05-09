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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeRemaining)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Error)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileUrl)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileHash)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeedLimitKBps)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveSpeedLimitKBps)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Priority)));
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
        FileType = FileInfo.Extension[1..];
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
    /// Gets the total size of the file.
    /// </summary>
    public long? Size => FileInfo?.Length;

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
        get => _fileHash;
        internal set
        {
            if (_fileHash == value)
            {
                return;
            }

            _fileHash = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileHash)));
        }
    }

    private string? _fileHash;

    /// <summary>
    /// Gets or sets the error string.
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
    /// Gets or sets the priority.
    /// </summary>
    public int Priority { get; set; }

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
