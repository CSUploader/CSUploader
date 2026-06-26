// <copyright file="NitroFlarePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// NitroFlare (nitroflare.com) upload pipeline. Architecturally a twin of <see cref="HitFilePipeline"/>:
/// a reCAPTCHA-gated WebView sign-in yields a stable, per-account <b>40-hex user hash</b> that is the
/// entire durable upload credential (no cookies ride the upload), persisted in
/// <see cref="Dal.FileHosterLoginDto.ApiKey"/>. Verified against the live site (capture 2026-06-27):
/// <list type="number">
///   <item><b>Sign-in.</b> Login at <c>nitroflare.com/login</c> is Google-reCAPTCHA gated, so this
///   drives a WebView2 sign-in (<see cref="IInteractiveAuthService"/>). The signed-in page then
///   fetches its own upload form (<c>/plugins/fileupload/index.php</c>, which carries
///   <c>formData: { user: '&lt;40-hex&gt;' }</c>) and the account page (<c>/member?s=account</c>,
///   for the email) with its own cookie jar — handing both back via the probe, so no cookie
///   capture/forwarding is needed on the C# side.</item>
///   <item><b>Discover the upload server.</b> <c>GET /plugins/fileupload/getServer</c> returns the
///   assigned storage node as a bare-text URL (<c>https://sNN.nitroflare.com:8443/index.php</c>);
///   public (no auth), assigned per request, so it runs fresh for each file.</item>
///   <item><b>Upload.</b> Browser-shaped multipart POST to that URL: the file under <c>files[]</c>
///   and <c>user=&lt;hash&gt;</c> — cookieless; the hash is the sole link to the account. Response
///   <c>{"files":[{"url":"https://nitroflare.com/view/&lt;code&gt;/&lt;name&gt;", ...}]}</c>; the
///   <c>url</c> is the share link, used verbatim.</item>
/// </list>
/// Free-tier per-file cap is 10 GiB (established 2026-06-27); storage is effectively unlimited, so no
/// quota is surfaced. No anonymous upload — the user hash requires an account.
/// </summary>
public sealed class NitroFlarePipeline : IFileHosterPipeline
{
    private const string SiteOrigin = "https://nitroflare.com";
    private const string SiteReferer = "https://nitroflare.com/";
    private const string GetServerUrl = "https://nitroflare.com/plugins/fileupload/getServer";

    // WebView sign-in. The reCAPTCHA login lives on nitroflare.com; the session cookie is named
    // `user` (distinct from the `user` UPLOAD field, which is the 40-hex hash). We don't key
    // completion on the cookie — the signed-in PAGE fetches the upload hash + email itself (the
    // probe below), carrying its own jar (PHPSESSID/user/randHash) automatically.
    private const string LoginUrl = "https://nitroflare.com/login";
    private const string CookieDomain = ".nitroflare.com";
    private const string SessionCookieName = "user"; // unused with the probe; the spec requires a name

    // JS run in the signed-in page each poll tick. Fetches the upload form for the durable 40-hex
    // user hash (only present once authenticated) and the account page for the login email — both
    // credentialed, same-origin. Returns "" until the hash is known, then a JSON string
    // {"hash":"…","email":"…"|null}; the WebView completes the moment it returns non-empty. Inits once.
    private const string HashProbeScript = """
        (function () {
          if (!window.__csuNF) {
            window.__csuNF = true;
            window.__csuNFout = '';
            var done = function (hash, email) { window.__csuNFout = JSON.stringify({ hash: hash, email: email }); };
            var getEmail = function (cb) {
              fetch('/member?s=account', { credentials: 'include' })
                .then(function (r) { return r.ok ? r.text() : ''; })
                .then(function (t) { var m = t.match(/[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}/); cb(m ? m[0] : null); })
                .catch(function () { cb(null); });
            };
            var getHash = function () {
              fetch('/plugins/fileupload/index.php?time=' + Date.now(), { credentials: 'include' })
                .then(function (r) { return r.ok ? r.text() : ''; })
                .then(function (t) {
                  var m = t.match(/user:\s*['"]([a-f0-9]{40})['"]/i);
                  if (m) { getEmail(function (email) { done(m[1], email); }); }
                  else { setTimeout(getHash, 1500); }
                })
                .catch(function () { setTimeout(getHash, 1500); });
            };
            getHash();
          }
          return window.__csuNFout;
        })();
        """;

    private readonly IInteractiveAuthService? _authService;
    private readonly Func<string, Task<HttpResponseSnapshot>>? _getOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public NitroFlarePipeline(IInteractiveAuthService? authService = null)
    {
        _authService = authService;
    }

    /// <summary>Test ctor — drives the getServer GET and the multipart upload from canned responses
    /// so the discovery/parse logic can be exercised without the network.</summary>
    internal NitroFlarePipeline(
        Func<string, HttpResponseSnapshot> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _getOverride = url => Task.FromResult(getOverride(url));
        _uploadOverride = uploadOverride;
    }

    public string Name => "NitroFlare";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>
    /// Free-tier per-file cap: 10 GiB. The limit was given as "10 GB" (2026-06-27); we read that as
    /// binary (10 × 1024³) because the upload endpoint is PHP (<c>…/index.php</c>) and PHP's size
    /// suffix (<c>10G</c>) is binary, so the server's real cap is almost certainly 10 GiB. NitroFlare
    /// has no storage-quota pre-flight, so this client cap is what keeps an oversized file from
    /// uploading in full only to be rejected. If the host turns out to mean decimal 10 GB, lower this
    /// to <c>10L * 1000 * 1000 * 1000</c> (a file in the 10 GB–10 GiB window would otherwise waste an
    /// upload).
    /// </summary>
    public long? MaxFileSize => 10L * 1024 * 1024 * 1024;

    public int? MaxFilesPerPackage => null;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Resolve the account credential (the durable user hash) up front ===
        // NitroFlare uploads require an account: the user hash, captured at sign-in into the ApiKey
        // slot, is the sole credential. Fail before any bytes if it's missing (anonymous DTO or an
        // account that was never signed in) rather than POSTing a hashless upload the node rejects.
        if (ctx.Credentials.IsAnonymous || string.IsNullOrWhiteSpace(ctx.Credentials.ApiKey))
        {
            yield return new AttemptFailed(
                "NitroFlare account isn't signed in. Open Settings → Accounts and Sign in to the NitroFlare account before uploading.",
                null);
            yield break;
        }

        string userHash = ctx.Credentials.ApiKey.Trim();

        // === Discover a fresh upload node, then upload ===
        (string? uploadUrl, string? discoverError) = await DiscoverUploadServerAsync(ctx);
        if (uploadUrl is null)
        {
            yield return new AttemptFailed(discoverError ?? "NitroFlare upload server discovery failed", null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

        // Bridge HttpHandler.UploadProgress -> TransferProgress via an unbounded channel
        // (can't yield from inside the event handler).
        Channel<UploadEvent> progressChannel = Channel.CreateUnbounded<UploadEvent>();
        EventHandler<OperationProgressEventArgs> onProgress = (_, e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, (double)e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, uploadUrl, userHash);

        _ = uploadTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= onProgress;

        // Let any transport fault propagate to the shared retry layer (AttemptRunner): a mid-send
        // abort or a connect-phase failure to the per-request sNN node arrives as a safe-to-retry
        // UploadBodyTransferException, and re-running this whole pipeline discovers a FRESH node — the
        // node never double-creates because the body never finished sending. A user cancel surfaces as
        // OperationCanceledException (classified by AttemptRunner). A SERVER VERDICT never throws
        // (UploadMultipartAsync returns the snapshot), so it parses below.
        HttpResponseSnapshot uploadResponse = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(uploadResponse);
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    /// <summary>
    /// Initial sign-in / "Check / Refresh". NitroFlare's login is reCAPTCHA-gated, so the first
    /// sign-in drives a WebView2 modal whose page fetches the account's 40-hex upload hash (and login
    /// email) itself. A later "Check / Refresh" arrives with the previously-stored hash in
    /// <paramref name="apiKey"/>: the hash is the entire durable upload credential and there's nothing
    /// to re-validate offline, so the account stays valid WITHOUT re-opening a browser.
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = password;
        _ = handler; // the embedded page fetches the hash itself (HashProbeScript); no C# HTTP needed

        string? storedHash = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

        // Already have a hash but reached CheckAccountAsync (a pasted hash, or an account from before
        // capture) → keep it valid without a WebView; the hash is durable.
        if (storedHash is not null)
        {
            return new AccountCheckResult(true, AccountType.Free, "NitroFlare account ready.", ApiKey: storedHash);
        }

        if (_authService is null)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "NitroFlare sign-in needs the desktop app's embedded browser (to solve the reCAPTCHA). Alternatively paste your account's upload hash.");
        }

        InteractiveAuthSpec spec = new(
            HosterName: Name,
            LoginUrl: LoginUrl,
            CookieDomain: CookieDomain,
            CookieName: SessionCookieName,
            SuccessProbeScript: HashProbeScript);

        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(spec, username, proxy, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "NitroFlare sign-in failed: " + ex.Message);
        }

        (string? hash, string? email) = ParseProbeResult(captured?.ProbeValue);
        if (string.IsNullOrEmpty(hash))
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "NitroFlare sign-in was cancelled, or didn't complete before the window was closed.");
        }

        // The page returned the account's upload hash (the durable credential) and its login email
        // (DerivedUsername → the account's displayed name). NitroFlare exposes no upload-storage quota,
        // so storage stays null → the grid shows no Used/Available for it.
        return new AccountCheckResult(
            true,
            AccountType.Free,
            "Signed in to NitroFlare.",
            ApiKey: hash,
            DerivedUsername: email);
    }

    /// <summary>
    /// Parses the probe's JSON payload (<c>{"hash":"&lt;40-hex&gt;","email":"…"|null}</c>) into the
    /// account's upload hash and login email. Returns nulls for a missing/garbage payload so the
    /// caller fails the sign-in cleanly.
    /// </summary>
    internal static (string? Hash, string? Email) ParseProbeResult(string? probeValue)
    {
        if (string.IsNullOrWhiteSpace(probeValue))
        {
            return (null, null);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(probeValue);
            JsonElement root = doc.RootElement;
            string? hash = root.TryGetProperty("hash", out JsonElement h) && h.ValueKind == JsonValueKind.String
                ? h.GetString()
                : null;
            string? email = root.TryGetProperty("email", out JsonElement e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() is { Length: > 0 } s ? s.Trim() : null
                : null;
            return (string.IsNullOrEmpty(hash) ? null : hash, string.IsNullOrEmpty(email) ? null : email);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// GETs <c>/plugins/fileupload/getServer</c>, which returns the assigned storage node as a bare
    /// HTTPS URL (no JSON). Validates it's an absolute https nitroflare.com URL before returning, so a
    /// garbage/error body fails BEFORE any bytes are sent (never a wasted upload to a bad server).
    /// </summary>
    private async Task<(string? UploadUrl, string? Error)> DiscoverUploadServerAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot snap;
        try
        {
            snap = _getOverride is not null
                ? await _getOverride(GetServerUrl)
                : await ctx.Handler.GetSnapshotAsync(GetServerUrl, BrowserHeaders(), ctx.Cancellation);
        }
        catch (Exception ex)
        {
            return (null, "NitroFlare upload-server discovery failed: " + ex.Message);
        }

        if (snap.StatusCode is < 200 or >= 300)
        {
            return (null, $"NitroFlare getServer returned HTTP {snap.StatusCode}: {Snippet(snap.Body)}");
        }

        string candidate = (snap.Body ?? string.Empty).Trim();
        if (!IsValidUploadServer(candidate))
        {
            return (null, $"NitroFlare getServer did not return a valid upload server: {Snippet(snap.Body)}");
        }

        return (candidate, null);
    }

    /// <summary>True for an absolute <c>https</c> URL on a <c>nitroflare.com</c> host — the shape
    /// getServer is documented to return (<c>https://sNN.nitroflare.com:8443/index.php</c>).</summary>
    internal static bool IsValidUploadServer(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.EndsWith(".nitroflare.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("nitroflare.com", StringComparison.OrdinalIgnoreCase));

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string uploadUrl, string userHash)
    {
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal)
        {
            ["user"] = userHash,
        };

        Dictionary<string, string> headers = BrowserHeaders();

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, uploadUrl, extraFields, headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            uploadUrl,
            fileFieldName: "files[]",
            extraFields: extraFields,
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    private static Dictionary<string, string> BrowserHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = SiteOrigin,
        ["Referer"] = SiteReferer,
    };

    /// <summary>
    /// Success is <c>{"files":[{"url":"https://nitroflare.com/view/&lt;code&gt;/&lt;name&gt;", …}]}</c>
    /// → the share link, used verbatim. A missing/error file entry surfaces any server message so
    /// size/policy rejections are legible.
    /// </summary>
    private static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("files", out JsonElement files)
                && files.ValueKind == JsonValueKind.Array
                && files.GetArrayLength() > 0)
            {
                JsonElement first = files[0];
                string? url = first.TryGetProperty("url", out JsonElement u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return (url, null);
                }

                // A file entry with no url — surface its error field when present.
                string? fileError = first.TryGetProperty("error", out JsonElement fe) && fe.ValueKind == JsonValueKind.String ? fe.GetString() : null;
                if (!string.IsNullOrWhiteSpace(fileError))
                {
                    return (null, $"NitroFlare upload failed: {fileError}");
                }
            }

            return (null, $"NitroFlare upload returned an unexpected response (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }
        catch (JsonException)
        {
            return (null, $"NitroFlare upload returned an unexpected response (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }
    }

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
