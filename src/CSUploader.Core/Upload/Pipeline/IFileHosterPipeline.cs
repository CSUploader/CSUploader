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
/// proxy selection, <see cref="HttpHandler"/> construction, logging hookup,
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
    public string Name { get; }

    /// <summary>True when the hoster needs the file's content hash before upload (e.g. Rapidgator MD5).</summary>
    public bool RequiresHashingBeforeUpload { get; }

    /// <summary>True when the hoster computes a hash post-upload (rare, usually false).</summary>
    public bool RequiresHashingAfterUpload { get; }

    /// <summary>
    /// Maximum file size (in bytes) the hoster accepts, or null when no hard limit is
    /// declared. Used by the wizard to warn before a package is queued and by
    /// <see cref="RunAsync"/> to fail-fast oversized files before any bytes are sent.
    /// Free vs. premium splits aren't modelled here — the most restrictive (free-tier)
    /// limit is what callers should treat as authoritative.
    /// </summary>
    public long? MaxFileSize { get; }

    /// <summary>
    /// Maximum number of files per package the hoster's upload session accepts, or null
    /// when no limit applies. Enforced at wizard time; the runner doesn't know the
    /// package shape so this is purely a UX-side guard.
    /// </summary>
    public int? MaxFilesPerPackage { get; }

    /// <summary>
    /// Maximum number of this hoster's uploads that may run SIMULTANEOUSLY for the given account, or null
    /// when no per-hoster limit applies (the default). The upload scheduler never launches more than this
    /// many concurrent uploads for the hoster — on top of the global and (optional) per-host concurrency
    /// settings. Varies by tier for some hosters (e.g. ufile: free 10, pro 30, business 99).
    /// </summary>
    public int? MaxConcurrentUploadsFor(Dal.FileHosterLoginDto credentials) => null;

    /// <summary>
    /// True when the hoster accepts uploads with no account/login. The upload wizard offers
    /// such hosters a built-in "Anonymous" option that needs no Accounts/Settings entry — the
    /// runner passes a blank <see cref="Dal.FileHosterLoginDto"/> (no username) and the
    /// pipeline takes its anonymous path. Defaults to false; only hosters that genuinely
    /// support unauthenticated upload (currently GigaPeta) override it.
    /// </summary>
    public bool SupportsAnonymousUpload => false;

    /// <summary>
    /// Whether this hoster has accounts <b>at all</b>. Defaults to true — nearly every host does.
    /// <para>
    /// Set false for a drop host with no login anywhere on the site (GigaFile, temp.sh, Litterbox,
    /// tmpfiles.org, qu.ax, DropMeFiles, transfer.it, wormhole.app). The Add Account dialog leaves
    /// those out of its hoster list: offering one is offering to add something that cannot exist, and
    /// the only outcome is a check that fails with "this host has no accounts".
    /// </para>
    /// <para>
    /// This is NOT the inverse of <see cref="SupportsAnonymousUpload"/> — plenty of hosts do both
    /// (catbox, gofile, ufile, upload.ee, UpZur), so an anonymous-capable host still belongs in the
    /// dialog when an account would buy the user something.
    /// </para>
    /// </summary>
    public bool SupportsAccounts => true;

    /// <summary>
    /// Per-file size cap (bytes, null = none) for a specific selected account. Defaults to the
    /// account-independent <see cref="MaxFileSize"/>; pipelines whose cap varies by tier
    /// override this. The wizard's oversize guard and <see cref="RunAsync"/>'s fail-fast both
    /// consult it with the attempt's credentials — e.g. Hexload's anonymous tier allows 2 GiB
    /// where its API tier uses the smaller default.
    /// </summary>
    public long? MaxFileSizeFor(Dal.FileHosterLoginDto credentials) => MaxFileSize;

    /// <summary>
    /// How long the hoster keeps an uploaded file for the given account, for the wizard's "Kept for"
    /// column. Defaults to <see cref="FileRetention.Unspecified"/> — "this host publishes no retention
    /// period we have verified", which is the honest answer for most of them and is NOT a claim of
    /// permanence. Override only with what the host itself states: its own copy, its plan table, or an
    /// expiry stamp measured off a real upload — and cite which, in the pipeline's remarks.
    /// <para>
    /// Takes the account because retention is tiered more often than not: TeraBytez keeps an anonymous
    /// file 5 days and a registered one 30, upload.ee 50 days against 120. Where only one tier is
    /// documented, return <see cref="FileRetention.Unspecified"/> for the others rather than assuming
    /// the figure carries over.
    /// </para>
    /// </summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials) => FileRetention.Unspecified;

    /// <summary>
    /// Whether a FREE/ANONYMOUS downloader must solve a captcha before this hoster hands over a
    /// shared file — the wizard's "Download captcha?" column. This models the host's ordinary
    /// free/anonymous download flow and intentionally ignores the uploader's credentials, so there
    /// is no parameter. Defaults to <see cref="DownloadCaptchaRequirement.Unknown"/> ("not
    /// verified", not "no captcha"); pipelines override only with what was verified, citing the
    /// source in remarks (full research matrix: <c>docs/hoster-download-captcha.md</c>).
    /// </summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Unknown;

    /// <summary>
    /// Returns null when the hoster accepts a file named <paramref name="fileName"/>, or a short
    /// user-facing reason when the hoster's server would REJECT the name outright — independent of
    /// size or count (e.g. a disallowed character). The name-analog of <see cref="MaxFileSize"/>: the
    /// wizard's Summary step drops such files from the hoster exactly like an oversized file (so they
    /// surface in the orphan banner) and <see cref="RunAsync"/> fails fast on them before any bytes.
    /// Defaults to null — no hoster restricts names unless it overrides this (currently only
    /// Buzzheavier, which rejects <c>#</c> and <c>;</c>).
    /// </summary>
    public string? RejectedFileNameReason(string fileName) => null;

    /// <summary>
    /// Returns null when the hoster accepts this file's TYPE, or a short user-facing reason when its
    /// extension is one the hoster refuses — whether by blocklist (Uploadrar bars video, filedot bars
    /// images) or allowlist (qu.ax permits only a named set, so <c>.r00</c>, <c>.sfv</c> and
    /// <c>.nfo</c> are out).
    /// <para>
    /// Separate from <see cref="RejectedFileNameReason"/> on purpose: that one is about CHARACTERS in
    /// the name (Buzzheavier's <c>#</c> and <c>;</c>), and the wizard tells the user so. Reporting an
    /// extension rule under that wording would say "these names use characters this hoster won't
    /// accept" about a perfectly ordinary <c>rls.r00</c>, which sends the user hunting for a character
    /// that isn't there. Two rules, two sentences.
    /// </para>
    /// <para>
    /// Consumed in the same two places: the wizard drops such files from this hoster's Summary column
    /// and names them on the hoster step, and the pipeline fails fast before sending bytes.
    /// </para>
    /// </summary>
    public string? RejectedFileExtensionReason(string fileName) => null;

    /// <summary>
    /// Runs the protocol-specific portion of an upload attempt. Yields events for progress
    /// and outcomes. Must terminate with no more than one of <see cref="TransferCompleted"/>,
    /// <see cref="AttemptFailed"/>, or <see cref="AttemptCancelled"/> — the runner adds the
    /// <see cref="AttemptCompleted"/> envelope itself.
    /// </summary>
    public IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, CancellationToken ct);

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
    public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct);
}
