// <copyright file="FileRetention.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// What a hoster's retention period is counted FROM. The distinction is not cosmetic: a host that
/// deletes 30 days after the last download keeps an actively-fetched file forever, while one that
/// deletes 30 days after upload takes it away regardless.
/// </summary>
public enum FileRetentionBasis
{
    /// <summary>No retention period is known for this hoster (the default).</summary>
    Unspecified = 0,

    /// <summary>The hoster keeps files with no expiry.</summary>
    Permanent,

    /// <summary>Files are deleted a fixed time after they are UPLOADED.</summary>
    AfterUpload,

    /// <summary>Files are deleted a fixed time after their LAST DOWNLOAD, so traffic keeps them alive.</summary>
    AfterLastDownload,
}

/// <summary>
/// How long a hoster keeps an uploaded file, as the wizard's "Kept for" column reports it.
/// <para>
/// <b>Only what the host itself states.</b> <see cref="Unspecified"/> — the default every pipeline
/// gets — means "this host publishes no retention period we have verified", NOT "permanent". Most
/// hosts here fall in that bucket, and guessing permanence for them would be a claim about someone
/// else's storage policy that nothing in a capture supports. Where a host does say (its own copy, its
/// plan table, or an <c>expires</c> stamp measured off a real upload), the pipeline overrides
/// <see cref="IFileHosterPipeline.RetentionFor"/> and cites the source in its remarks.
/// </para>
/// </summary>
/// <param name="Basis">What the period is counted from, or that there is none.</param>
/// <param name="Duration">
/// How long files last. Null for <see cref="FileRetentionBasis.Unspecified"/> and
/// <see cref="FileRetentionBasis.Permanent"/>, which have no duration to report.
/// </param>
public readonly record struct FileRetention(FileRetentionBasis Basis, TimeSpan? Duration)
{
    /// <summary>The default: this hoster publishes no retention period. Not a claim of permanence.</summary>
    public static FileRetention Unspecified => default;

    /// <summary>The hoster keeps files indefinitely — only for hosts that say so (catbox, udrop) or
    /// where this app explicitly asks for no expiry and the host confirmed it (qu.ax, DropMB).</summary>
    public static FileRetention Permanent => new(FileRetentionBasis.Permanent, null);

    /// <summary>Files are deleted <paramref name="duration"/> after upload, whatever the traffic.</summary>
    public static FileRetention AfterUpload(TimeSpan duration) => new(FileRetentionBasis.AfterUpload, duration);

    /// <summary>Files are deleted <paramref name="duration"/> after their last download.</summary>
    public static FileRetention AfterLastDownload(TimeSpan duration)
        => new(FileRetentionBasis.AfterLastDownload, duration);

    /// <summary>Convenience for the common "N days after upload" case.</summary>
    public static FileRetention DaysAfterUpload(double days) => AfterUpload(TimeSpan.FromDays(days));

    /// <summary>Convenience for the common "N days after the last download" case.</summary>
    public static FileRetention DaysAfterLastDownload(double days) => AfterLastDownload(TimeSpan.FromDays(days));

    /// <summary>True when this hoster deletes files at some point — i.e. there is a countdown to
    /// report. False for both <see cref="Unspecified"/> and <see cref="Permanent"/>.</summary>
    public bool Expires => Duration is not null;

    /// <summary>
    /// Sort key for the wizard's "Kept for" column, in hours: longer retention sorts higher,
    /// <see cref="Permanent"/> sorts above every finite period, and <see cref="Unspecified"/> is null
    /// so unknown rows group together rather than pretending to be the shortest.
    /// </summary>
    public double? SortKey => Basis switch
    {
        FileRetentionBasis.Permanent => double.PositiveInfinity,
        FileRetentionBasis.Unspecified => null,
        _ => Duration?.TotalHours,
    };
}
