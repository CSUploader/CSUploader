// <copyright file="FileStorePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// FileStore (filestore.me) — stock XFileSharing on the classic web-form upload, <b>account-only,
/// 250 MB</b> — but it does NOT derive from <see cref="XFileSharingApiPipeline"/>, and the reason is
/// the whole story of this host:
/// <para>
/// <b>Its apex is Cloudflare-challenged to this client and its upload nodes are not.</b> Every page
/// and API route on <c>filestore.me</c> (and <c>www.</c>) answers this app <c>403</c> with
/// <c>Cf-Mitigated: challenge</c> and <c>cType: 'interactive'</c> — the sign-in form, <c>?op=upload_form</c>,
/// <c>?op=api_get_limits</c>, <c>/api/*</c>, all of it. <c>srvN.filestore.me</c>, where the bytes
/// actually go, is plain Apache with no Cloudflare at all and answers normally. The family base
/// assumes the opposite — it fetches the apex upload page on every attempt and reads the apex account
/// page on every check — so every one of its paths would break here.
/// </para>
/// <para>
/// <b>What makes it work anyway:</b> on this host the form's <c>sess_id</c> is exactly the <c>xfss</c>
/// cookie, so the sign-in browser — which passes the challenge, being a browser — can hand over
/// everything an upload needs. The sign-in probe script fetches <c>?op=upload_form</c> <b>inside the
/// page</b> and returns both the node URL and the session; after that this app talks only to the node.
/// Verified end to end: a 2 MB file posted straight to <c>srv9</c> with that session answered
/// <c>[{"file_status":"OK","file_code":"…"}]</c>.
/// </para>
/// <para>
/// <b>⚠ The account cannot be re-checked from here</b> — every page that could confirm a session is
/// behind the challenge. <see cref="RefreshAccountAsync"/> therefore reports what it knows rather than
/// pretending: an account keeps its stored session until an upload proves otherwise, and a lapsed one
/// surfaces as an upload failure telling the user to sign in again. Reporting "invalid" instead would
/// auto-disable working accounts on a check this app is simply unable to perform.
/// </para>
/// <para>
/// <b>Anonymous upload is refused</b> — measured at the node, not read off a page:
/// <c>utype=anon</c> answers <c>[{"file_status":"uploads are not enabled for your account type",
/// "file_code":"undef"}]</c>. Note the shape: HTTP 200, ordinary XFS JSON, and <c>undef</c> where the
/// code goes.
/// </para>
/// <para>
/// ⚠ <b>Nodes are interchangeable but individually flaky</b>: <c>srv2</c>, <c>srv9</c> and
/// <c>srv10</c> all accepted the same session, while <c>srv1</c> answered <c>500 "Server don't allow
/// uploads at the moment"</c>. The node is captured at sign-in rather than guessed, and a refusal from
/// it says to re-check the account — which re-runs the probe and picks up whatever node the site hands
/// out next.
/// </para>
/// </summary>
public sealed class FileStorePipeline : IFileHosterPipeline, ISessionRefreshablePipeline
{
    private const string Host = "https://filestore.me";

    /// <summary>The upload page's own <c>max_upload_filesize: '250'</c>. Binary, as XFileSharing's
    /// limits are 1024-based.</summary>
    private const long MaxFileSizeBytes = 250L * 1024 * 1024;

    /// <summary>What the login 302 issues <c>xfss</c> for. The family rarely states one; seven days is
    /// the horizon the base assumes for the same cookie.</summary>
    private const int SessionLifetimeDays = 7;

    /// <summary>
    /// Runs in the sign-in browser on every poll tick. The first tick starts the fetch and parks its
    /// result in a global; later ticks return it — the shape HitFile's probe uses, because the WebView
    /// wants a string back, not a promise.
    /// <para>
    /// It fetches <c>?op=upload_form</c> <b>from inside the page</b>, which is the entire trick: the
    /// browser passes the Cloudflare challenge that this app's HTTP stack cannot, so the node URL and
    /// the session come out of a page request rather than out of a request we make ourselves. Failure
    /// clears the latch so the next tick tries again — the window opens before the user has signed in.
    /// </para>
    /// </summary>
    private const string SessionProbeScript = """
        (function () {
          if (!window.__csuFS) {
            window.__csuFS = true;
            window.__csuFSout = '';
            fetch('/?op=upload_form', { credentials: 'include' })
              .then(function (r) { return r.ok ? r.text() : null; })
              .then(function (h) {
                if (!h || h.indexOf('/logout/') < 0) { window.__csuFS = false; return; }
                var a = h.match(/<form[^>]*action="([^"]*upload\.cgi[^"]*)"/i);
                var s = h.match(/name="sess_id"[^>]*value="([^"]+)"/i);
                if (!a || !s) { window.__csuFS = false; return; }
                window.__csuFSout = JSON.stringify({ node: a[1], sess: s[1] });
              })
              .catch(function () { window.__csuFS = false; });
          }
          return window.__csuFSout;
        })()
        """;

    private readonly IInteractiveAuthService? _authService;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _uploadOverride;

    public FileStorePipeline(IInteractiveAuthService? authService = null)
    {
        _authService = authService;
    }

    /// <summary>Test ctor — stubs the file upload, the only HTTP this pipeline makes.</summary>
    internal FileStorePipeline(
        IInteractiveAuthService? authService,
        Func<string, string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _authService = authService;
        _uploadOverride = uploadOverride;
    }

    public string Name => "FileStore";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>Measured at the node: <c>utype=anon</c> earns "uploads are not enabled for your
    /// account type".</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.Credentials.IsAnonymous)
        {
            yield return new AttemptFailed(
                "FileStore has no anonymous upload — its node answers \"uploads are not enabled for your "
                + "account type\". Add a FileStore account in Account Manager.",
                null);
            yield break;
        }

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds FileStore's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        string? session = NullIfWhiteSpace(ctx.Credentials.SessionCookie);
        string? node = NullIfWhiteSpace(ctx.Credentials.ApiKey);
        if (session is null || node is null)
        {
            // Both come from the same sign-in, and neither can be recovered without it: this app
            // cannot reach the page that issues them.
            yield return new AttemptFailed(
                "The FileStore account has no saved sign-in. Sign in again from Account Manager — this host's "
                + "pages are behind a Cloudflare challenge, so the app's sign-in window is the only way to reach them.",
                null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, node, session);
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

        HttpResponseSnapshot? response = null;
        Exception? transferFault = null;
        try
        {
            response = await uploadTask;
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            transferFault = ex;
        }

        if (transferFault is not null)
        {
            yield return new AttemptFailed($"FileStore upload failed: {transferFault.Message}", transferFault);
            yield break;
        }

        (string? link, string? error) = ParseUploadResponse(response!);
        if (link is null)
        {
            yield return new AttemptFailed(error!, null);
            yield break;
        }

        yield return new TransferCompleted(link);
    }

    private Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string node, string session)
    {
        // The family's registered field set, as this host's own form posts it — its submit button is
        // the only part left out. sess_id IS the session cookie here, which is what lets an upload
        // happen without ever fetching the challenged form page.
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["sess_id"] = session,
            ["utype"] = "reg",
            ["link_rcpt"] = string.Empty,
            ["link_pass"] = string.Empty,
            ["to_folder"] = string.Empty,
            ["keepalive"] = "1",
        };

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
        };

        return _uploadOverride is not null
            ? _uploadOverride(ctx.FilePath, node, fields)
            : ctx.Handler.UploadMultipartAsync(
                ctx.FilePath, node, "file_0", ctx.SpeedBudget, fields, headers, ctx.Cancellation);
    }

    /// <summary>
    /// Reads the node's reply. ⚠ Every outcome arrives as <b>HTTP 200 with ordinary-looking JSON</b>;
    /// the verdict is <c>file_status</c>, and a refusal puts the literal <c>undef</c> where the code
    /// goes. Anything that trusted the status code would report a refusal as a successful upload.
    /// Internal for testing.
    /// </summary>
    internal static (string? Link, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"FileStore's upload node rejected the file (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, $"FileStore's upload reply wasn't JSON: {Snippet(response.Body)}");
        }

        JsonElement entry = root.ValueKind == JsonValueKind.Array
            ? (root.GetArrayLength() > 0 ? root[0] : default)
            : root;

        if (entry.ValueKind != JsonValueKind.Object)
        {
            return (null, $"FileStore's upload reply carried no result: {Snippet(response.Body)}");
        }

        string status = entry.TryGetProperty("file_status", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty;
        string code = entry.TryGetProperty("file_code", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty;

        if (!string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            // ⚠ Measured: a LAPSED SESSION produces this exact wording too, because an unknown
            // sess_id simply looks anonymous to the node — it has no way to say "your sign-in
            // expired". Repeating the host's words alone would send a user with a perfectly good
            // account hunting for an upgrade they don't need, so the likelier cause is named first.
            if (status.Contains("not enabled for your account type", StringComparison.OrdinalIgnoreCase))
            {
                return (null, "FileStore rejected the sign-in this upload used — most likely it has expired. "
                    + "Sign in again from Account Manager. (Its node words this as \"uploads are not enabled for "
                    + "your account type\", which is also what it tells a caller with no account at all.)");
            }

            // Otherwise the host's own words say far more than a generic failure would — a blocked
            // extension, a size refusal, a node that isn't taking uploads.
            return (null, $"FileStore refused the file: {(status.Length > 0 ? status : Snippet(response.Body))}");
        }

        return code.Length == 0 || code == "undef"
            ? (null, $"FileStore reported success but named no file: {Snippet(response.Body)}")
            : ($"{Host}/{code}", null);
    }

    /// <summary>
    /// Signs in through the app's browser window, which is the only thing that can reach this host's
    /// pages, and comes back with both halves of the credential: the session and the node.
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = password;
        _ = apiKey;
        _ = handler;   // the page fetches the upload form itself; no C# HTTP can get through here

        if (_authService is null)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "Signing in to FileStore needs the desktop app's embedded browser — its pages are behind a "
                + "Cloudflare challenge. Try again from the app.");
        }

        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(
                new InteractiveAuthSpec(
                    HosterName: Name,
                    LoginUrl: Host + "/login.html",
                    CookieDomain: ".filestore.me",
                    CookieName: "xfss",

                    // XFS stores the account name in its own cookie here, and no signed-in page
                    // renders it — without this the account would save with a blank name, which for a
                    // browser-sign-in hoster is the only name it would ever have.
                    UsernameCookieName: "login",
                    SuccessProbeScript: SessionProbeScript),
                username,
                proxy,
                ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "FileStore sign-in failed: " + ex.Message);
        }

        (string? node, string? session) = ParseProbeResult(captured?.ProbeValue);
        if (node is null || session is null)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "FileStore sign-in was cancelled, or didn't complete before the window was closed.");
        }

        return new AccountCheckResult(
            true,
            AccountType.Free,
            "Signed in to FileStore.",
            SessionCookie: session,
            SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(SessionLifetimeDays),

            // The node the site handed this session, kept because this app can't ask for one: the
            // page that names it is behind the challenge.
            ApiKey: node,
            DerivedUsername: NullIfWhiteSpace(captured?.CapturedUsername));
    }

    /// <summary>Reads the probe's <c>{"node":…,"sess":…}</c> payload. Internal for testing.</summary>
    internal static (string? Node, string? Session) ParseProbeResult(string? probeValue)
    {
        if (string.IsNullOrWhiteSpace(probeValue))
        {
            return (null, null);
        }

        try
        {
            JsonElement root = JsonDocument.Parse(probeValue).RootElement;
            string? node = root.TryGetProperty("node", out JsonElement n) ? NullIfWhiteSpace(n.GetString()) : null;
            string? session = root.TryGetProperty("sess", out JsonElement s) ? NullIfWhiteSpace(s.GetString()) : null;

            // A node that isn't one of this host's would mean the page changed shape under us, and
            // posting a file at whatever it named is worse than refusing.
            return node is not null && node.Contains(".filestore.me/", StringComparison.OrdinalIgnoreCase)
                ? (node, session)
                : (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// <b>Cannot verify anything</b>, and says so rather than guessing: every page that could confirm a
    /// session — <c>?op=my_files</c>, <c>?op=my_account</c>, the limits call — is behind the Cloudflare
    /// challenge this app can't pass. An account therefore keeps its stored sign-in until an upload
    /// proves otherwise, at which point the node's own refusal is what tells the user to sign in again.
    /// Reporting "invalid" here instead would auto-disable working accounts over a check that is
    /// impossible rather than failed.
    /// </summary>
    public Task<AccountCheckResult> RefreshAccountAsync(string? apiKey, string sessionCookie, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = handler;
        _ = proxy;
        _ = ct;

        bool usable = !string.IsNullOrWhiteSpace(sessionCookie) && !string.IsNullOrWhiteSpace(apiKey);

        return Task.FromResult(usable
            ? new AccountCheckResult(
                true,
                AccountType.Free,
                "Signed in to FileStore (this host's pages can't be re-checked from here — a lapsed sign-in "
                + "shows up on the next upload).",
                SessionCookie: sessionCookie,
                SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(SessionLifetimeDays),
                ApiKey: apiKey)
            : new AccountCheckResult(false, AccountType.Free, "The FileStore account has no saved sign-in — sign in again."));
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Snippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty response)";
        }

        string trimmed = body.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }
}
