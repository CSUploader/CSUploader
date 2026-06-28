// <copyright file="IAccountVerifier.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;

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
    /// <summary>
    /// Verifies credentials against a hoster. <paramref name="apiKey"/> takes precedence
    /// when supplied — for hosters with key-based APIs (currently Ex-Load) the verifier
    /// will validate via the API and skip any cookie/WebView paths. When
    /// <paramref name="apiKey"/> is null, <paramref name="username"/>/<paramref name="password"/>
    /// are used; pipelines that can derive an API key from those credentials (Ex-Load's
    /// my_account scrape) will surface it on <see cref="AccountCheckResult.ApiKey"/>.
    /// <paramref name="sessionCookie"/>, when supplied (a previously-captured login session), lets
    /// a <see cref="Pipeline.ISessionRefreshablePipeline"/> re-validate / refresh server-side data
    /// without re-opening the WebView — HitFile re-reads its storage usage through the proxy with it.
    /// </summary>
    public Task<AccountCheckResult> CheckAsync(string hosterName, string username, string password, string? apiKey = null, string? sessionCookie = null, CancellationToken ct = default);

    /// <summary>
    /// Re-reads an account's current storage usage WITHOUT any interactive sign-in (no WebView) — for
    /// the upload wizard's Summary page to refresh free-space figures before fitting files. Only
    /// hosters whose pipeline implements <see cref="Pipeline.IStorageRefreshablePipeline"/> (IcerBox,
    /// FileBoom, HitFile) can refresh; for any other hoster, a missing/expired stored session, or a
    /// transport failure it returns null so the caller keeps the last-known snapshot.
    /// </summary>
    public Task<Pipeline.StorageUsage?> RefreshStorageAsync(string hosterName, FileHosterLoginDto credentials, CancellationToken ct = default);
}
