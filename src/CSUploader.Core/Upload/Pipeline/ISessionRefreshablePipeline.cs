// <copyright file="ISessionRefreshablePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Opt-in capability for a pipeline whose "Check / Refresh" can re-read server-side account
/// data directly from C# using a previously-captured login session cookie — instead of
/// re-opening the interactive WebView. Implemented by hosters whose durable credential can't
/// reach the data on its own: HitFile's permanent upload <c>appId</c> can't read storage usage,
/// so refresh replays the logged-in <c>.hitfile.net</c> cookies (captured at sign-in) against the
/// storage API through the same proxy uploads use.
/// </summary>
/// <remarks>
/// <see cref="AccountVerifier"/> calls <see cref="RefreshAccountAsync"/> instead of
/// <see cref="IFileHosterPipeline.CheckAccountAsync"/> only when BOTH the pipeline implements this
/// interface AND a non-empty session cookie is available for the account. The initial sign-in (no
/// stored cookie) and pipelines that don't implement this still go through
/// <see cref="IFileHosterPipeline.CheckAccountAsync"/>. Implementations must degrade gracefully when
/// the session has expired — keep the account valid (the durable credential is untouched) and simply
/// omit the refreshed data so the caller preserves the last-known values.
/// </remarks>
public interface ISessionRefreshablePipeline
{
    /// <summary>
    /// Re-reads the account's server-side data using <paramref name="sessionCookie"/> (a
    /// <c>name=value; name=value</c> header captured at sign-in), routed through
    /// <paramref name="handler"/>/<paramref name="proxy"/> so the issuing IP matches the account's.
    /// </summary>
    /// <param name="apiKey">The account's durable credential (e.g. HitFile's appId), echoed back on
    /// the result so a successful-but-session-expired refresh keeps the account intact.</param>
    /// <param name="sessionCookie">Non-empty captured login cookies for the account.</param>
    /// <param name="handler">Proxied HTTP handler (built without <c>UseCookies</c>, so the cookie is
    /// forwarded as a header).</param>
    /// <param name="proxy">The proxy the handler routes through (kept for parity with
    /// <see cref="IFileHosterPipeline.CheckAccountAsync"/>; the session's issuing IP is the proxy IP).</param>
    /// <param name="ct">Cancellation.</param>
    public Task<AccountCheckResult> RefreshAccountAsync(string? apiKey, string sessionCookie, HttpHandler handler, ProxyChoice proxy, CancellationToken ct);
}
