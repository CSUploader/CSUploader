// <copyright file="IStorageRefreshablePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// A pipeline that can re-read an account's current storage usage WITHOUT any interactive sign-in
/// (no WebView / captcha) — using only the credentials' already-stored secrets (a session cookie, a
/// JWT, or a non-captcha username/password login). The upload wizard's Summary page calls this to
/// refresh free-space figures before fitting files, so it must never pop a browser or block on a
/// challenge. Implemented only by the storage-reporting hosters (IcerBox, FileBoom, HitFile).
/// </summary>
public interface IStorageRefreshablePipeline
{
    /// <summary>
    /// Re-reads the account's storage usage non-interactively. Returns the fresh figures, or
    /// <c>null</c> when it couldn't refresh (no usable stored session, an expired token, a transport
    /// error, or the hoster doesn't expose usage) — callers keep the last-known snapshot. Throws
    /// only <see cref="OperationCanceledException"/>; all other failures collapse to <c>null</c>.
    /// </summary>
    Task<StorageUsage?> RefreshStorageAsync(FileHosterLoginDto credentials, HttpHandler handler, ProxyChoice proxy, CancellationToken ct);
}

/// <summary>Current storage usage for an account, in bytes. Either field may be null when the hoster
/// doesn't expose it (e.g. HitFile reports usage but no quota — unlimited).</summary>
public readonly record struct StorageUsage(long? UsedBytes, long? QuotaBytes);
