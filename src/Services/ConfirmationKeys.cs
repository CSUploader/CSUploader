// <copyright file="ConfirmationKeys.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;

namespace CSUploader.Services;

/// <summary>
/// Stable identifiers for opt-out-able confirmation prompts. The key is persisted into the
/// <c>suppressedConfirmations</c> setting, so rename with care — existing users' suppressions
/// are keyed by these strings.
/// </summary>
public static class ConfirmationKeys
{
    public const string RemoveUploadPackageOrFile = "remove-upload-package-or-file";
    public const string RemoveUploadedEntry = "remove-uploaded-entry";
    public const string RemoveFileHosterAccount = "remove-file-hoster-account";

    /// <summary>
    /// Ordered list of all known confirmation keys with a short human label, used by the
    /// Settings page to let users re-enable prompts they previously dismissed.
    /// </summary>
    public static ReadOnlyCollection<(string Key, string Label)> All { get; } = new List<(string, string)>
    {
        (RemoveUploadPackageOrFile, "Remove package or file from Uploads tab"),
        (RemoveUploadedEntry, "Remove entries from Uploaded history"),
        (RemoveFileHosterAccount, "Remove a file hoster account"),
    }.AsReadOnly();
}
