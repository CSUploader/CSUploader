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
    public const string RemoveProxy = "remove-proxy";
    public const string ResetColumns = "reset-columns";

    /// <summary>
    /// Ordered list of all known confirmation keys paired with the ResX resource key for the
    /// user-visible label. Used by the Settings page to let users re-enable prompts they
    /// previously dismissed; the row VM resolves the label through <c>Localizer</c> so it
    /// updates live on culture change.
    /// </summary>
    public static ReadOnlyCollection<(string Key, string LabelResourceKey)> All { get; } = new List<(string, string)>
    {
        (RemoveUploadPackageOrFile, "Confirm_RemoveUploadPackageOrFile"),
        (RemoveUploadedEntry, "Confirm_RemoveUploadedEntry"),
        (RemoveFileHosterAccount, "Confirm_RemoveFileHosterAccount"),
        (RemoveProxy, "Confirm_RemoveProxy"),
        (ResetColumns, "Confirm_ResetColumns"),
    }.AsReadOnly();
}
