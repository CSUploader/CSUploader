// <copyright file="IAccountVerifier.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// Verifies a set of file-hoster credentials by performing a login round-trip. Used by
/// the Settings UI (Add Account, Refresh Account, Refresh All) to confirm credentials
/// work and to detect premium-expiry changes between runs.
/// </summary>
/// <remarks>
/// Lookup goes through <see cref="Pipeline.IFileHosterRegistry"/>, so any hoster with a
/// registered <see cref="Pipeline.IFileHosterPipeline"/> participates automatically.
/// Hosters without a pipeline return a "not implemented" result — the same string the
/// pre-pipeline static stub used to return, so existing UI paths keep working.
/// </remarks>
public interface IAccountVerifier
{
    Task<AccountCheckResult> CheckAsync(string hosterName, string username, string password, CancellationToken ct = default);
}
