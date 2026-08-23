// <copyright file="BuzzheavierPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Buzzheavier (buzzheavier.com) upload pipeline — anonymous OR account — built on the host's documented
/// "developer" upload API rather than its browser tus flow. A single raw HTTP <c>PUT</c> of the file body
/// to <c>https://w.buzzheavier.com/&lt;name&gt;</c>; the JSON response's <c>id</c> becomes the share link
/// <c>https://buzzheavier.com/&lt;id&gt;</c>. No hashing, no chunking, no captcha on the upload path
/// (Cloudflare only challenges the marketing/app pages, not the <c>w.</c> upload host).
/// <list type="number">
///   <item><b>Anonymous.</b> <c>PUT w.buzzheavier.com/&lt;url-encoded-name&gt;</c> with no auth. The
///   wizard offers this as the built-in "Anonymous" option (<see cref="SupportsAnonymousUpload"/>).</item>
///   <item><b>Account.</b> Same PUT plus <c>Authorization: Bearer &lt;accountId&gt;</c>. Buzzheavier has
///   no separate API token — the Bearer credential is literally the account id, which is not shown in the
///   UI but is returned by the authenticated <c>GET /api/account</c> (<c>data.id</c>). The id is captured
///   once at sign-in and persisted in <see cref="FileHosterLoginDto.ApiKey"/> — the entire durable upload
///   credential, exactly like <see cref="NitroFlarePipeline"/>'s user hash.</item>
/// </list>
/// Sign-in is Cloudflare-Turnstile gated, so <see cref="CheckAccountAsync"/> drives a WebView2 sign-in
/// (<see cref="IInteractiveAuthService"/>) whose signed-in page fetches <c>/api/account</c> itself and
/// hands back the account id via the probe — no cookie capture/forwarding on the C# side. A later
/// "Check / Refresh" re-uses the stored id without re-opening a browser (the id is durable). Buzzheavier
/// advertises no per-file size cap and no storage quota, so none is reported (the Accounts grid shows
/// "Unlimited" via <see cref="FileHosterClient.HasUnlimitedStorage"/>). One name quirk is enforced up
/// front: Buzzheavier's server rejects <c>#</c> and <c>;</c> in a filename, and over the raw-PUT dev API
/// that arrives as a mid-stream socket reset (masquerading as a size limit), so <see cref="RunAsync"/>
/// fails fast on such names before sending any bytes (see <see cref="RejectedFileNameReason"/>, which the
/// upload wizard also consults to drop such files at the Summary step). Verified against a
/// live capture (2026-07-08); the raw PUT reuses <see cref="HttpHandler.UploadPutAsync"/>, exactly as
/// Pixeldrain and Storage.to do.
/// </summary>
public sealed class BuzzheavierPipeline : IFileHosterPipeline
{
    // The developer-API upload host (distinct from the marketing site + the ts. tus host). A single raw
    // PUT of the file body lands here; only this host is on the upload path, and it carries no Cloudflare
    // challenge.
    private const string UploadHostPrefix = "https://w.buzzheavier.com/";
    private const string PublicUrlPrefix = "https://buzzheavier.com/";

    // WebView sign-in. Login is Cloudflare-Turnstile gated, so the signed-in PAGE fetches /api/account
    // (credentials:'include') itself and returns data.id — the durable Bearer credential. We don't key
    // completion on a cookie (xsession is HttpOnly and looks the same before/after in some flows).
    private const string LoginUrl = "https://buzzheavier.com/login";
    private const string CookieDomain = ".buzzheavier.com";
    private const string SessionCookieName = "xsession"; // unused with the probe; the spec requires a name

    // JS run in the signed-in page each poll tick. Once GET /api/account returns 200 with a data.id (only
    // served to an authenticated session), that id — the account's Bearer credential — flows back as the
    // probe value. Returns "" until authenticated, so the WebView completes the moment it's non-empty.
    private const string AccountIdProbeScript = """
        (function () {
          if (!window.__csuBZ) {
            window.__csuBZ = true;
            window.__csuBZout = '';
            var poll = function () {
              fetch('/api/account', { credentials: 'include' })
                .then(function (r) { return r.ok ? r.text() : ''; })
                .then(function (t) {
                  try {
                    var j = JSON.parse(t);
                    var id = j && j.data && j.data.id;
                    if (id) { window.__csuBZout = String(id); return; }
                  } catch (e) {}
                  setTimeout(poll, 1500);
                })
                .catch(function () { setTimeout(poll, 1500); });
            };
            poll();
          }
          return window.__csuBZout;
        })();
        """;

    private readonly IInteractiveAuthService? _authService;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public BuzzheavierPipeline(IInteractiveAuthService? authService = null)
    {
        _authService = authService;
    }

    /// <summary>Test ctor — drives the raw PUT from a canned response so the anonymous/account branch,
    /// the auth-header wiring, the link parse, and the failure/transport branches run without the network
    /// or a real file. An optional auth service exercises the WebView sign-in path in
    /// <see cref="CheckAccountAsync"/>.</summary>
    internal BuzzheavierPipeline(
        Func<string, string, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride,
        IInteractiveAuthService? authService = null)
    {
        _uploadOverride = uploadOverride;
        _authService = authService;
    }

    public string Name => "Buzzheavier";

    /// <summary>Downloads are captcha-free: a probe upload's public page downloads through a
    /// plain tokenized link straight to the bytes (live flow, 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>No hard per-file cap: Buzzheavier markets unlimited size, and testing (a 731&#160;MB file
    /// that first looked size-capped turned out to be a rejected filename, not a byte limit — see the
    /// type summary) found no ceiling on the raw-PUT path.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    /// <summary>Buzzheavier accepts uploads with no account — the wizard offers it as a built-in
    /// "Anonymous" option. An account links uploads to it via the Bearer account id.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // Buzzheavier's server-side name check rejects '#' and ';' (and only those — see
        // RejectedFileNameReason). Over the raw-PUT dev API that rejection isn't a clean body: the server
        // drops the socket mid-stream, which the shared retry layer sees as a retryable
        // UploadBodyTransferException and replays three full times, surfacing as a misleading "connection
        // forcibly closed". The wizard already drops such files at the Summary step, but a package queued
        // by other means can still reach here — so fail fast, before any bytes, with an actionable message.
        string? nameReason = RejectedFileNameReason(ctx.FileName);
        if (nameReason is not null)
        {
            yield return new AttemptFailed(nameReason, null);
            yield break;
        }

        // Account uploads carry the durable account id as a Bearer token (captured at sign-in into the
        // ApiKey slot). Fail before any bytes on a non-anonymous DTO with no stored id — otherwise the
        // authless PUT would silently land the file as an ANONYMOUS upload rather than in the account.
        string? bearer = null;
        if (!ctx.Credentials.IsAnonymous)
        {
            if (string.IsNullOrWhiteSpace(ctx.Credentials.ApiKey))
            {
                yield return new AttemptFailed(
                    "Buzzheavier account isn't signed in. Open Settings → Accounts and Sign in to the Buzzheavier account before uploading.",
                    null);
                yield break;
            }

            bearer = ctx.Credentials.ApiKey.Trim();
        }

        yield return new TransferStarted(ctx.FileSize);

        // Bridge HttpHandler.UploadProgress -> TransferProgress via an unbounded channel (can't yield from
        // inside the event handler) — same pattern as the other streaming pipelines.
        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, bearer);
        _ = uploadTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= OnProgress;

        // Let any transport fault propagate to the shared retry layer (AttemptRunner): until the body is
        // fully sent Buzzheavier has committed no file, so a mid-send abort arrives as a retryable
        // UploadBodyTransferException and a whole-pipeline retry re-uploads cleanly. A SERVER VERDICT does
        // NOT throw (UploadPutAsync returns the snapshot), so it parses below.
        HttpResponseSnapshot uploadResponse = await uploadTask;

        (string? id, string? error) = ParseUploadResponse(uploadResponse);
        if (id is null)
        {
            yield return new AttemptFailed(error ?? "Buzzheavier upload failed.", null);
            yield break;
        }

        yield return new TransferCompleted(PublicUrlPrefix + id);
    }

    /// <summary>
    /// Initial sign-in / "Check / Refresh". Buzzheavier's login is Cloudflare-Turnstile gated, so the
    /// first sign-in drives a WebView2 modal whose signed-in page fetches the account id from
    /// <c>/api/account</c> itself. A later "Check / Refresh" arrives with the stored id in
    /// <paramref name="apiKey"/>: the id is the whole durable credential and there's nothing to
    /// re-validate offline, so the account stays valid WITHOUT re-opening a browser.
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = password;
        _ = handler; // the embedded page fetches /api/account itself (the probe); no C# HTTP needed

        // Already have an account id (a pasted id, or an account signed in earlier) → keep it valid without
        // a WebView; the id is durable.
        string? storedId = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (storedId is not null)
        {
            return new AccountCheckResult(true, AccountType.Free, "Buzzheavier account ready.", ApiKey: storedId, DerivedUsername: DisplayNameFor(storedId, username));
        }

        if (_authService is null)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "Buzzheavier sign-in needs the desktop app's embedded browser (to pass the Cloudflare check). Alternatively paste your account id.");
        }

        InteractiveAuthSpec spec = new(
            HosterName: Name,
            LoginUrl: LoginUrl,
            CookieDomain: CookieDomain,
            CookieName: SessionCookieName,
            SuccessProbeScript: AccountIdProbeScript);

        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(spec, username, proxy, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "Buzzheavier sign-in failed: " + ex.Message);
        }

        string? accountId = captured?.ProbeValue?.Trim();
        if (string.IsNullOrEmpty(accountId))
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "Buzzheavier sign-in was cancelled, or didn't complete before the window was closed.");
        }

        // The page returned the account id (the durable Bearer credential). Buzzheavier exposes no email
        // or storage figure, so the id doubles as the display name and no usage is reported.
        return new AccountCheckResult(
            true,
            AccountType.Free,
            "Signed in to Buzzheavier.",
            ApiKey: accountId,
            DerivedUsername: DisplayNameFor(accountId, username));
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string? bearer)
    {
        string url = UploadHostPrefix + Uri.EscapeDataString(ctx.FileName);

        // Anonymous: no auth. Account: the account id as a Bearer token. No Origin/Referer — the developer
        // API is a plain curl-style endpoint, not a browser-origin request.
        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        if (bearer is not null)
        {
            headers["Authorization"] = "Bearer " + bearer;
        }

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, url, headers, ctx.SpeedBudget);
        }

        return await ctx.Handler.UploadPutAsync(
            ctx.FilePath,
            url,
            contentType: MimeTypeGuesser.Guess(ctx.FilePath),
            ctx.SpeedBudget,
            headers: headers,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Buzzheavier's name validator rejects exactly two ASCII characters — <c>#</c> (U+0023) and
    /// <c>;</c> (U+003B) — returning <c>400 "the Name field is invalid"</c>. Confirmed 2026-07-09 by
    /// probing the live <c>/api/upload</c> validator across every character a Windows filename can legally
    /// contain plus common CJK/fullwidth punctuation: every other character — including <c>[ ] @ &amp; + =
    /// , ( ) $ % ' ^ { }</c>, spaces, and the fullwidth look-alikes <c>＃ ；</c> — is accepted. Returns an
    /// actionable message naming the offending character(s), or <c>null</c> when the name is fine.
    /// Implements <see cref="IFileHosterPipeline.RejectedFileNameReason"/>, so the upload wizard drops
    /// such files at the Summary step (like an oversized file) and <see cref="RunAsync"/> fails fast.
    /// </summary>
    public string? RejectedFileNameReason(string fileName)
    {
        bool hasHash = fileName.Contains('#', StringComparison.Ordinal);
        bool hasSemicolon = fileName.Contains(';', StringComparison.Ordinal);
        if (!hasHash && !hasSemicolon)
        {
            return null;
        }

        (string offenders, string verb) = (hasHash, hasSemicolon) switch
        {
            (true, true) => ("'#' and ';'", "are"),
            (true, false) => ("'#'", "is"),
            _ => ("';'", "is"),
        };

        return $"Buzzheavier rejected the filename: {offenders} {verb} not allowed. Rename the file and try again.";
    }

    /// <summary>
    /// Success is a 2xx whose JSON carries the new file id — <c>{"code":201,"data":{"id":"&lt;id&gt;"}}</c>
    /// (the browser flow's shape) or a bare <c>{"id":"&lt;id&gt;"}</c>. Returns the id (share link
    /// <c>buzzheavier.com/&lt;id&gt;</c>), or an error with a body snippet. A 401/403 is called out as a
    /// likely stale/absent account id so the message is actionable.
    /// </summary>
    private static (string? Id, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is >= 200 and < 300)
        {
            string? id = TryReadId(response.Body);
            if (!string.IsNullOrEmpty(id))
            {
                return (id, null);
            }

            return (null, $"Buzzheavier upload returned an unexpected response (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        if (response.StatusCode is 401 or 403)
        {
            return (null, $"Buzzheavier rejected the upload (HTTP {response.StatusCode}) — the account id may be stale; re-sign in to the account. {Snippet(response.Body)}");
        }

        return (null, $"Buzzheavier upload failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
    }

    /// <summary>Reads the file id from either <c>data.id</c> (preferred) or a top-level <c>id</c>. Null
    /// when the body isn't JSON or carries no string id.</summary>
    private static string? TryReadId(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("id", out JsonElement dataId)
                && dataId.ValueKind == JsonValueKind.String)
            {
                return dataId.GetString();
            }

            return root.TryGetProperty("id", out JsonElement topId) && topId.ValueKind == JsonValueKind.String
                ? topId.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Buzzheavier exposes no email/username, so the Accounts grid shows the account id — unless
    /// the user typed a name when adding the account, which is preferred.</summary>
    private static string? DisplayNameFor(string accountId, string? enteredUsername)
        => string.IsNullOrWhiteSpace(enteredUsername) ? accountId : enteredUsername;

    private static string Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty)";
        }

        string trimmed = body.Trim()
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }
}
