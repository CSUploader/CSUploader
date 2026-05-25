// <copyright file="IFileHosterPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Per-hoster strategy. Implementations own their auth shape (token, cookie, OAuth, API
/// key, anything) — <see cref="AttemptRunner"/> never inspects credentials beyond passing
/// them in via <see cref="AttemptContext.Credentials"/>.
/// </summary>
/// <remarks>
/// <para>
/// Cross-cutting concerns the runner has already handled before <see cref="RunAsync"/>:
/// proxy selection, <see cref="Lib.Net.Http.HttpHandler"/> construction, logging hookup,
/// cancellation propagation. Implementations must use <c>ctx.Handler</c> for all HTTP —
/// it is non-null by type and pre-configured with the chosen proxy.
/// </para>
/// <para>
/// Implementations are typically singletons holding per-credentials caches (e.g. a
/// <c>ConcurrentDictionary&lt;int, AuthState&gt;</c> keyed by <c>Credentials.Id</c>) so
/// the same login is reused across files. Cache invalidation on auth failure is the
/// pipeline's responsibility.
/// </para>
/// </remarks>
/// <example>
/// <para><b>Token-based</b> (Rapidgator-style): cache <c>(token, expiry)</c> per credentials id;
/// invalidate on 401; pass token via query param or bearer header.</para>
/// <para><b>Cookie-based</b>: cache a <see cref="System.Net.CookieContainer"/> per credentials id;
/// the runner-supplied <c>HttpHandler</c> is constructed without `UseCookies`, so the
/// pipeline must attach cookies to outbound requests itself or use a hoster-internal
/// HttpClient adorned with the cached jar.</para>
/// <para><b>API-key</b>: no auth state needed beyond <see cref="AttemptContext.Credentials"/>;
/// every request includes the key in a header. <see cref="AuthStarted"/>/<see cref="AuthSucceeded"/>
/// can be skipped entirely.</para>
/// <para><b>OAuth2 with refresh</b>: cache <c>(access_token, refresh_token, expiry)</c>; on
/// expiry try refresh first, then full re-login.</para>
/// </example>
public interface IFileHosterPipeline
{
    /// <summary>Hoster name, must match the key used by <see cref="IFileHosterRegistry"/>.</summary>
    string Name { get; }

    /// <summary>True when the hoster needs the file's content hash before upload (e.g. Rapidgator MD5).</summary>
    bool RequiresHashingBeforeUpload { get; }

    /// <summary>True when the hoster computes a hash post-upload (rare, usually false).</summary>
    bool RequiresHashingAfterUpload { get; }

    /// <summary>
    /// Maximum file size (in bytes) the hoster accepts, or null when no hard limit is
    /// declared. Used by the wizard to warn before a package is queued and by
    /// <see cref="RunAsync"/> to fail-fast oversized files before any bytes are sent.
    /// Free vs. premium splits aren't modelled here — the most restrictive (free-tier)
    /// limit is what callers should treat as authoritative.
    /// </summary>
    long? MaxFileSize { get; }

    /// <summary>
    /// Maximum number of files per package the hoster's upload session accepts, or null
    /// when no limit applies. Enforced at wizard time; the runner doesn't know the
    /// package shape so this is purely a UX-side guard.
    /// </summary>
    int? MaxFilesPerPackage { get; }

    /// <summary>
    /// Runs the protocol-specific portion of an upload attempt. Yields events for progress
    /// and outcomes. Must terminate with no more than one of <see cref="TransferCompleted"/>,
    /// <see cref="AttemptFailed"/>, or <see cref="AttemptCancelled"/> — the runner adds the
    /// <see cref="AttemptCompleted"/> envelope itself.
    /// </summary>
    IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, CancellationToken ct);

    /// <summary>
    /// Verifies a set of credentials against the hoster. Used by the Settings UI to confirm
    /// an account works before it's saved (and on Refresh to detect expired premium / kicked
    /// sessions). Implementations should perform a real round-trip — typically a login —
    /// and surface premium state, expiry, and a short human-readable message. The supplied
    /// <paramref name="handler"/> is created with the next proxy from the rotation (or
    /// <see cref="Lib.Net.ProxyChoice.Direct"/> when proxies are disabled) and disposed by
    /// the caller. <paramref name="proxy"/> is the same proxy choice the handler was built
    /// from, surfaced separately for pipelines that need the raw selection (e.g. to route
    /// an embedded browser through the same proxy for captcha-gated sign-ins).
    /// </summary>
    /// <param name="apiKey">Optional API key for hosters that support key-based REST APIs
    /// (currently Ex-Load). When non-null the pipeline should verify via the API and
    /// skip any cookie/WebView paths. When null the pipeline uses
    /// <paramref name="username"/>/<paramref name="password"/> and may derive an API key
    /// from them — when it does, the resulting key is surfaced on
    /// <see cref="AccountCheckResult.ApiKey"/> so the caller can persist it onto the DTO.</param>
    Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct);
}
