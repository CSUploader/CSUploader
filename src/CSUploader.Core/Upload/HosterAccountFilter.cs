// <copyright file="HosterAccountFilter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// How the upload wizard's File Hosters step filters the list by upload mode. Replaces the earlier
/// "Anonymous only" checkbox, which could express two of these three states but never the third.
/// <para>
/// The two narrowing modes read the hoster's declared CAPABILITIES, and they are not each other's
/// inverse: <see cref="Upload.Pipeline.IFileHosterPipeline.SupportsAccounts"/> says so outright —
/// catbox, gofile, ufile, upload.ee and UpZur take anonymous uploads AND offer accounts, so they
/// belong under either narrowing. Filtering "account" as "not anonymous" would hide exactly those.
/// </para>
/// <para>
/// Neither mode asks whether the user actually HAS an account for the hoster. That is a different
/// question, already answered by the row's own affordances (a padlock and a greyed row where
/// nothing can be uploaded) — see <c>FileHosterSelectionViewModel.CanUse</c>.
/// </para>
/// </summary>
public enum HosterAccountFilter
{
    /// <summary>No narrowing — every hoster is listed. The default, and what "Clear filter" returns to.</summary>
    Both,

    /// <summary>Only hosters that accept uploads with no account at all.</summary>
    AnonymousOnly,

    /// <summary>Only hosters that offer accounts. Includes the anonymous-capable ones that also do.</summary>
    AccountOnly,
}
