// <copyright file="TurbobitPipeline.cs" company="CSUploader">
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
/// Turbobit (turbobit.net) — account upload. Same operator and the same SPA platform as the shipped
/// <see cref="HitFilePipeline"/>: identical endpoints, identical <c>kohanasession7</c> session cookie,
/// identical upload shape. Built from a browser capture of a signed-in upload 2026-08-01.
/// <list type="number">
///   <item><b>Sign in.</b> WebView at <c>turbobit.net/login</c>. Nothing in the cookie jar
///   distinguishes signed-in from anonymous, so — exactly as HitFile does — the signed-in PAGE is
///   asked to fetch its own <c>appId</c> (<c>POST /api/user/app/id</c>, which carries the HttpOnly
///   session automatically) and hand it back. That appId is the durable upload credential and is
///   stored in the ApiKey slot, so it can also be pasted directly.</item>
///   <item><b>Discover.</b> <c>POST app.turbobit.net/api/upload/urls</c> with <c>{"count":1}</c> →
///   <c>{"urls":["https://sNNN.turbobit.net/uploadfile"]}</c> — a fresh storage node per file.</item>
///   <item><b>Upload.</b> Multipart POST to that node: <c>Filedata</c>, <c>apptype=fd1</c>,
///   <c>folder_id=0</c> and <c>user_id=&lt;appId&gt;</c> → <c>{"result":true,"id":"…"}</c>.</item>
/// </list>
/// <para>
/// <b>The one value that differs from HitFile is <c>apptype</c>: <c>fd1</c> here, <c>fd2</c> there.</b>
/// It identifies the web app to the storage node, so copying HitFile's value would post to the wrong
/// application.
/// </para>
/// <para>
/// <b>Account-only, deliberately.</b> HitFile's pipeline also serves anonymous uploads (omit
/// <c>user_id</c>) and Turbobit probably does too, but its guest cap is documented as 200 MB — smaller
/// than a single part of a typical release — so an anonymous option here would mostly produce failed
/// uploads. It is left off until someone verifies a guest upload and wants it.
/// </para>
/// <para>
/// <b>Storage usage is not reported.</b> HitFile derives it by having the page recursively walk
/// <c>/api/folder/content</c> summing human-formatted sizes; Turbobit answers that endpoint the same
/// way (verified in the capture), so it can be added later. It is omitted here rather than duplicating
/// ~200 lines of walker for a figure the upload path doesn't need — if a third sibling of this
/// platform ever appears, extracting a shared base is the right move instead.
/// </para>
/// <para>
/// No declared per-file cap is exposed by the API or the SPA, so <see cref="MaxFileSize"/> is null and
/// the server's own refusal is the authority.
/// </para>
/// </summary>
public sealed class TurbobitPipeline : IFileHosterPipeline
{
    private const string DiscoveryUrl = "https://app.turbobit.net/api/upload/urls";

    /// <summary>count:1 — one storage node per file; discovered fresh for every upload.</summary>
    private const string DiscoveryBody = """{"count":1}""";

    /// <summary>The web app's id the storage node expects. HitFile's is <c>fd2</c> — do not share.</summary>
    private const string AppType = "fd1";

    private const string SiteOrigin = "https://turbobit.net";
    private const string SiteReferer = "https://turbobit.net/";

    /// <summary>Share links are <c>turbobit.net/&lt;id&gt;.html</c> — the site's long-standing public
    /// form. The upload response carries only the bare id, and the SPA builds the URL client-side, so
    /// this is the one detail taken from Turbobit's conventional link format rather than observed on
    /// the wire.</summary>
    private const string DownloadBase = "https://turbobit.net/";
    private const string DownloadSuffix = ".html";

    private const string LoginUrl = "https://turbobit.net/login";
    private const string CookieDomain = ".turbobit.net";

    /// <summary>Unused with the probe (the appId is the credential), but the spec requires a name.</summary>
    private const string SessionCookieName = "kohanasession7";

    private const string ApiBase = "https://app.turbobit.net/api";
    private const string CookieCaptureUrl = "https://app.turbobit.net/";

    /// <summary>
    /// JS run in the signed-in page on each poll tick, doing exactly what the SPA does: a credentialed
    /// <c>POST /api/user/app/id</c>, which carries the HttpOnly session automatically and answers with
    /// the appId only once authenticated. Returns "" until then — the WebView completes the moment it
    /// returns non-empty — so the window closes on real authentication rather than on a cookie that
    /// looks the same either way. The account's login email rides along as the CORS-exposed
    /// <c>x-logged-in</c> header, which is why it is only ever paired with a real appId.
    /// </summary>
    private const string AppIdProbeScript = """
        (function () {
          if (!window.__csuTB) {
            window.__csuTB = true;
            window.__csuTBout = '';
            window.__csuTBuser = null;
            var API = 'https://app.turbobit.net/api';
            var getAppId = function () {
              fetch(API + '/user/app/id', { method: 'POST', credentials: 'include' })
                .then(function (r) {
                  if (!r.ok) { return null; }
                  var u = r.headers.get('x-logged-in');
                  if (u) { window.__csuTBuser = u; }
                  return r.json();
                })
                .then(function (d) {
                  if (d && d.appId) {
                    window.__csuTBout = JSON.stringify({ appId: d.appId, username: window.__csuTBuser });
                  } else { setTimeout(getAppId, 1500); }
                })
                .catch(function () { setTimeout(getAppId, 1500); });
            };
            getAppId();
          }
          return window.__csuTBout;
        })();
        """;

    private readonly IInteractiveAuthService? _authService;
    private readonly Func<string, string, Task<HttpResponseSnapshot>>? _postJsonOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public TurbobitPipeline(IInteractiveAuthService? authService = null)
    {
        _authService = authService;
    }

    /// <summary>Test ctor — drives discovery and the upload from canned responses.</summary>
    internal TurbobitPipeline(
        Func<string, string, Task<HttpResponseSnapshot>> postJsonOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _postJsonOverride = postJsonOverride;
        _uploadOverride = uploadOverride;
    }

    public string Name => "Turbobit";

    /// <summary>Free downloads are captcha-gated: its own free-download SPA chunk gates the
    /// link behind FreeDownloadCaptchaView (FreePage bundle, 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>No cap is declared by the API or the SPA; the server decides.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    /// <summary>Account-only — see the class remarks for why the anonymous path isn't offered.</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        string? appId = string.IsNullOrWhiteSpace(ctx.Credentials.ApiKey) ? null : ctx.Credentials.ApiKey!.Trim();
        if (appId is null)
        {
            yield return new AttemptFailed(
                "Turbobit needs an account — open Settings → Accounts and sign in (or paste the account's upload id).",
                null);
            yield break;
        }

        // === Discover this file's storage node ===
        (string? uploadUrl, string? discoverError) = await DiscoverUploadServerAsync(ctx);
        if (uploadUrl is null)
        {
            yield return new AttemptFailed(discoverError!, null);
            yield break;
        }

        // === Upload ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, uploadUrl, appId);

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

        // A transport fault propagates raw to AttemptRunner, which re-runs this pipeline and discovers
        // a FRESH node — nothing is committed until the node answers, so no double-create.
        HttpResponseSnapshot response = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(response);
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = password;
        _ = handler; // the embedded page fetches the appId itself; no C# HTTP needed

        string? storedAppId = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

        // A pasted (or previously captured) appId is the durable credential and there's nothing to
        // re-validate offline, so keep the account valid WITHOUT opening a WebView.
        if (storedAppId is not null)
        {
            return new AccountCheckResult(true, AccountType.Free, "Turbobit account ready.", ApiKey: storedAppId);
        }

        if (_authService is null)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "Turbobit sign-in needs the desktop app's embedded browser. Alternatively paste your account's upload id.");
        }

        InteractiveAuthSpec spec = new(
            HosterName: Name,
            LoginUrl: LoginUrl,
            CookieDomain: CookieDomain,
            CookieName: SessionCookieName,
            SuccessProbeScript: AppIdProbeScript,
            CookieCaptureUrl: CookieCaptureUrl);

        InteractiveAuthResult? captured;
        try
        {
            captured = await _authService.AcquireSessionCookieAsync(spec, username, proxy, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "Turbobit sign-in failed: " + ex.Message);
        }

        (string? appId, string? email) = ParseProbeResult(captured?.ProbeValue);
        if (string.IsNullOrEmpty(appId))
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "Turbobit sign-in was cancelled, or didn't complete before the window was closed.");
        }

        string? sessionCookie = string.IsNullOrEmpty(captured?.SessionCookieValue) ? null : captured!.Value.SessionCookieValue;
        return new AccountCheckResult(
            true,
            AccountType.Free,
            "Signed in to Turbobit.",
            ApiKey: appId,
            SessionCookie: sessionCookie,
            DerivedUsername: email);
    }

    /// <summary>Reads <c>{"appId":…,"username":…}</c> back out of the probe. Internal for testing.</summary>
    internal static (string? AppId, string? Username) ParseProbeResult(string? probeValue)
    {
        if (string.IsNullOrWhiteSpace(probeValue))
        {
            return (null, null);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(probeValue);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? appId = Read(doc.RootElement, "appId");
            return string.IsNullOrWhiteSpace(appId) ? (null, null) : (appId, Read(doc.RootElement, "username"));
        }
        catch (JsonException)
        {
            return (null, null);
        }

        static string? Read(JsonElement obj, string name)
            => obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private async Task<(string? UploadUrl, string? Error)> DiscoverUploadServerAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot snap;
        try
        {
            snap = _postJsonOverride is not null
                ? await _postJsonOverride(DiscoveryUrl, DiscoveryBody)
                : await ctx.Handler.PostJsonAsync(DiscoveryUrl, DiscoveryBody, ctx.Cancellation);
        }
        catch (Exception ex)
        {
            return (null, "Turbobit upload-server discovery failed: " + ex.Message);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(snap.Body);
            // Guard the kind before GetString(): a mismatch throws InvalidOperationException, not
            // JsonException, and would escape the catch below as a raw crash.
            if (doc.RootElement.TryGetProperty("urls", out JsonElement urls)
                && urls.ValueKind == JsonValueKind.Array
                && urls.GetArrayLength() > 0
                && urls[0].ValueKind == JsonValueKind.String
                && urls[0].GetString() is { Length: > 0 } first)
            {
                return (first, null);
            }
        }
        catch (JsonException)
        {
            // fall through to the shared error below
        }

        return (null, $"Turbobit did not return an upload URL (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string uploadUrl, string appId)
    {
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal)
        {
            ["apptype"] = AppType,
            ["folder_id"] = "0",
            ["user_id"] = appId,
        };

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = SiteOrigin,
            ["Referer"] = SiteReferer,
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, uploadUrl, extraFields, headers, ctx.SpeedBudget);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            uploadUrl,
            fileFieldName: "Filedata",
            extraFields: extraFields,
            headers: headers,
            speedBudget: ctx.SpeedBudget,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Success is <c>{"result":true,"id":"&lt;code&gt;","message":"Everything is ok"}</c> → the share
    /// link. A <c>result:false</c> (or a missing id) surfaces the server's own <c>message</c> so
    /// size/policy refusals stay legible.
    /// </summary>
    private static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;
            bool ok = root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.True;
            string? id = root.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            if (ok && !string.IsNullOrWhiteSpace(id))
            {
                return (DownloadBase + id + DownloadSuffix, null);
            }

            string? message = root.TryGetProperty("message", out JsonElement msgEl) && msgEl.ValueKind == JsonValueKind.String ? msgEl.GetString() : null;
            return (null, $"Turbobit upload failed: {message ?? Snippet(response.Body)} (HTTP {response.StatusCode})");
        }
        catch (JsonException)
        {
            return (null, $"Turbobit upload returned an unreadable response (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }
    }

    private static string Snippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string trimmed = body.Trim()
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }
}
