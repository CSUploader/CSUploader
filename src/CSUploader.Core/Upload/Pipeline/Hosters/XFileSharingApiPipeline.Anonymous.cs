// <copyright file="XFileSharingApiPipeline.Anonymous.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// The ANONYMOUS (no-login web-form) upload path, as a partial: one class across five files, one
/// concern each — a file split, not a decomposition (see the main file's class doc).
/// </summary>
public abstract partial class XFileSharingApiPipeline
{
    /// <summary>
    /// Anonymous upload path for hosters that set <see cref="SupportsAnonymousUpload"/>. No
    /// API, no login: GET the web upload form to discover the per-session upload server, POST
    /// the file exactly as the browser's anonymous form does (empty <c>sess_id</c>,
    /// <c>utype=anon</c>), then parse the same <c>[{file_code, file_status}]</c> JSON the API
    /// path returns. Mirrors <see cref="RunAsync"/>'s upload/progress/parse machinery.
    /// </summary>
    /// <remarks>
    /// The homepage hands out a rotating upload server and some assignments resolve to dead CDN
    /// domains (observed: hexload.com served an unresolvable <c>*.drewimplemnt.top</c> while a
    /// retry got a live <c>*.droply.top</c>). On a connection/DNS failure — which happens before
    /// any bytes are sent, so nothing is wasted — we re-fetch a fresh server and retry, bounded
    /// by <see cref="AnonymousServerAttempts"/>.
    /// </remarks>
    private async IAsyncEnumerable<UploadEvent> RunAnonymousAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TransferStarted(ctx.FileSize);

        HttpRequestException? lastUnreachable = null;
        bool retriedNodeFailure = false;

        for (int attempt = 0; attempt < AnonymousServerAttempts; attempt++)
        {
            (string? uploadUrl, string? discoverError) = await DiscoverAnonymousServerAsync(ctx, ct);
            if (uploadUrl is null)
            {
                yield return new AttemptFailed(discoverError!, null);
                yield break;
            }

            // Progress bridge (same pattern as the API path).
            var progressChannel = Channel.CreateUnbounded<UploadEvent>();
            void onProgress(object? _, OperationProgressEventArgs e) =>
                progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
            ctx.Handler.UploadProgress += onProgress;

            Task<HttpResponseSnapshot> uploadTask = AnonymousUploadAsync(ctx, uploadUrl);

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

            bool cancelled = false;
            bool unreachable = false;
            Exception? exception = null;
            HttpResponseSnapshot? response = null;
            try
            {
                response = await uploadTask;
            }
            catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (HttpRequestException hre) when (IsServerUnreachable(hre))
            {
                // The assigned upload server didn't resolve/connect — no bytes were sent, so
                // grabbing a fresh server and retrying wastes nothing.
                lastUnreachable = hre;
                unreachable = true;
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            if (cancelled)
            {
                yield return new AttemptCancelled();
                yield break;
            }

            if (unreachable)
            {
                if (attempt < AnonymousServerAttempts - 1)
                {
                    ctx.Logger.Log(this, LogType.Status, $"{Name}: anonymous upload server unreachable ({lastUnreachable!.Message}); retrying with a fresh server.");
                    continue;
                }

                yield return new AttemptFailed(
                    $"{Name}: anonymous upload servers were unreachable after {AnonymousServerAttempts} attempts (last: {lastUnreachable!.Message}). "
                    + "The hoster rotates upload servers and handed out unresolvable ones — try again.",
                    lastUnreachable);
                yield break;
            }

            if (exception is not null)
            {
                yield return new AttemptFailed(exception.Message, exception);
                yield break;
            }

            (string? url, string? error, bool _) = ParseUploadResponse(NormalizeUploadResponse(response!));
            if (url is not null)
            {
                yield return new TransferCompleted(url);
                yield break;
            }

            // A node whose own backend broke — its fs.cgi answering 500, a gateway error — is a bad
            // draw from the rotating pool, not a verdict on the file, and a fresh node usually works
            // (observed on uploady.io: "failed while requesting fs.cgi: …500 Internal Server Error").
            // Retry ONCE only: unlike the dead-DNS retry above this one re-sends the entire file, so
            // it must not become a loop — and the predicate stays narrow so a real rejection
            // ("File too big") is never re-uploaded to be refused again.
            if (!retriedNodeFailure && attempt < AnonymousServerAttempts - 1 && IsTransientNodeFailure(error))
            {
                retriedNodeFailure = true;
                ctx.Logger.Log(this, LogType.Status, $"{Name}: upload node reported a backend failure ({error}); retrying once with a fresh server.");
                continue;
            }

            yield return new AttemptFailed(error ?? $"{Name}: anonymous upload returned no download link", null);
            yield break;
        }
    }

    /// <summary>
    /// True when an upload's <c>file_status</c> describes the NODE failing rather than the file being
    /// refused. XFileSharing nodes proxy the bytes on to their storage CGI and surface its failure
    /// verbatim, so a broken node reads as e.g. <c>failed while requesting fs.cgi: …500 Internal Server
    /// Error…</c>. Deliberately narrow: everything it does not match (quota, size, extension, "File too
    /// big") is a verdict that re-uploading would only earn again, at the cost of the whole file.
    /// <para>
    /// <c>No file on disk (/var/www/cgi-bin/temp/NN/CGItempNNNNN)</c> belongs here too, seen on
    /// uploady.io under 20 parallel uploads: the node took the bytes into its own CGI spool and the
    /// spool file was gone by the time it went to store them. The path it names is the node's scratch
    /// space, not anything of ours, so it says nothing about the file we sent.
    /// </para>
    /// <para>
    /// All three upload paths consult this — anonymous, web-form and API-key. Nothing about a broken
    /// node is specific to how the caller authenticated, and the API path went without the retry for
    /// a while purely because the fault was first diagnosed on the web-form one.
    /// </para>
    /// </summary>
    private static bool IsTransientNodeFailure(string? error)
        => error is not null
           && (error.Contains("fs.cgi", StringComparison.OrdinalIgnoreCase)
               || error.Contains("Internal Server Error", StringComparison.OrdinalIgnoreCase)
               || error.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase)
               || error.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase)
               || error.Contains("No file on disk", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fresh-server attempts for an anonymous upload before giving up. The homepage rotates the
    /// assigned upload server and a large share are dead (resolve to 0.0.0.0 / NODATA — observed
    /// ~half on hexload.com), so each attempt re-fetches a cache-busted homepage for a different
    /// server. Five tries makes hitting a live one near-certain while wasting nothing — dead
    /// servers fail at DNS/connect, before any bytes are sent.
    /// </summary>
    private const int AnonymousServerAttempts = 5;

    /// <summary>Sent on the homepage GET alongside the cache-buster query — belt-and-suspenders
    /// against any intermediary that honours request no-cache. <c>protected</c> so a fork that
    /// resolves its node from somewhere other than that page (UpZur reads <c>?op=api_get_limits</c>,
    /// having no form to scrape) can keep the same anti-caching stance.</summary>
    protected static readonly Dictionary<string, string> NoCacheHeaders = new(StringComparer.Ordinal)
    {
        ["Cache-Control"] = "no-cache",
        ["Pragma"] = "no-cache",
    };

    /// <summary>
    /// GETs the web upload form and scrapes the per-session upload server's <c>action</c> URL
    /// (the rotating <c>…/cgi-bin/upload.cgi?…</c>). Returns (null, error) when the homepage
    /// fetch fails or no upload form is present.
    /// </summary>
    /// <summary>
    /// Resolves the node an anonymous upload POSTs to. The family's usual way is to scrape the
    /// rotating <c>action</c> off the web upload form (below), but a hoster that exposes a keyless
    /// <c>/api/upload/server</c> can override this with the JSON call instead — a stable contract,
    /// where the HTML page is subject to WAF/marketing variation (Send.now does exactly that).
    /// </summary>
    protected virtual async Task<(string? UploadUrl, string? Error)> DiscoverAnonymousServerAsync(AttemptContext ctx, CancellationToken ct)
    {
        // Cache-bust the form page: it's cached per-connection/edge, so a plain re-GET hands back
        // the SAME (often dead) upload server — defeating the retry. A unique query param forces a
        // fresh assignment so each attempt actually tries a different server.
        string url = BuildAnonUploadFormUrl(Guid.NewGuid().ToString("N"));

        string html;
        try
        {
            html = await GetAsync(ctx, url, NoCacheHeaders, ct);
        }
        catch (Exception ex)
        {
            return (null, $"{Name}: anonymous upload form fetch failed: {ex.Message}");
        }

        Match m = _anonUploadActionRegex.Match(html);
        if (m.Success)
        {
            return (m.Groups[1].Value, null);
        }

        if (LooksLikeCloudflareChallenge(html))
        {
            return (null,
                $"{Name}: Cloudflare is serving this client its \"Just a moment…\" challenge instead of the "
                + "upload page. A managed challenge validates the browser itself (TLS fingerprint, JS "
                + "execution), so no header or cookie sent from here can satisfy it — the host is "
                + "effectively unavailable to this app while the challenge is applied to you.");
        }

        // Include what actually came back. GetAsync returns the body whatever the status is, so an
        // edge/WAF answer (an "Attention Required" 1015 rate-limit page, a geo block) reads as a plain
        // "form not found" — the snippet is what tells those apart from a genuine template change,
        // without needing a packet capture.
        return (null,
            $"{Name}: anonymous upload form (a <form action=\"…/upload.cgi…\">) not found at {url} "
            + $"(received {html.Length} bytes: {Snippet(html)})");
    }

    /// <summary>
    /// True when a response body is a Cloudflare challenge interstitial rather than the requested
    /// page. Worth naming explicitly: the wall it represents (a <c>managed</c> challenge fingerprints
    /// the HTTP client) is one this app provably cannot pass — the cf_clearance-forwarding path that
    /// exists on this base was built, tested and found insufficient against managed challenges
    /// (TakeFile/UbiqFile) — so telling the user that outright beats an inscrutable parse failure.
    /// </summary>
    protected static bool LooksLikeCloudflareChallenge(string body)
        => body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
           || body.Contains("_cf_chl_opt", StringComparison.Ordinal)
           || body.Contains("cf-mitigated", StringComparison.OrdinalIgnoreCase)
           || body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The page carrying the anonymous upload form, with <paramref name="cacheBuster"/> mixed into the
    /// query (see <see cref="DiscoverAnonymousServerAsync"/> — a cached page would defeat the
    /// rotating-server retry). Defaults to the homepage, which is where the family renders it
    /// (Hexload, Send.now, DropGalaxy). Override for hosters that render the anonymous form ONLY on
    /// the upload page (uploady.io: its homepage carries no form at all).
    /// </summary>
    protected virtual string BuildAnonUploadFormUrl(string cacheBuster) => $"{Host}/?_={cacheBuster}";

    /// <summary>
    /// True when an upload POST failed because the server couldn't be reached at all — DNS
    /// resolution or TCP connect failed, i.e. before any bytes were sent. Safe to retry against
    /// a freshly-assigned server. A mid-stream failure (bytes already in flight) is NOT this and
    /// is surfaced as a normal failure so a partially-uploaded file is never re-sent.
    /// </summary>
    private static bool IsServerUnreachable(HttpRequestException ex)
        => ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
           || ex.InnerException is System.Net.Sockets.SocketException;

    private Task<HttpResponseSnapshot> AnonymousUploadAsync(AttemptContext ctx, string uploadUrl)
    {
        Dictionary<string, string> fields = BuildAnonymousExtraFields();
        Dictionary<string, string> headers = BrowserAnonymousHeaders();

        if (_uploadOverride is not null)
        {
            return _uploadOverride(ctx.FilePath, uploadUrl, fields, headers, ctx.SpeedBudget);
        }

        return ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            uploadUrl,
            fileFieldName: "file_0",
            ctx.SpeedBudget,
            extraFields: fields,
            headers: headers,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Exact field set the browser posts for an anonymous upload (captured from hexload.com
    /// 2026-06-13, in this order): <c>utype=anon</c> + an empty <c>sess_id</c> are what
    /// distinguish it from the logged-in classic POST. The empties must be present — the
    /// XFileSharing multipart parser is field-presence sensitive (see brupload-multipart-quirks).
    /// <para>
    /// <c>protected virtual</c> for the same reason <see cref="BuildClassicExtraFields"/> is: a fork
    /// whose live upload was proven with a different set replicates ITS set rather than trusting a
    /// near-miss to be equivalent.
    /// </para>
    /// </summary>
    protected virtual Dictionary<string, string> BuildAnonymousExtraFields() => new(StringComparer.Ordinal)
    {
        ["sess_id"] = string.Empty,
        ["utype"] = "anon",
        ["mode"] = string.Empty,
        ["file_public"] = string.Empty,
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
        ["keepalive"] = "1",
    };

    /// <summary>
    /// Headers for the anonymous upload POST. Cross-site (the upload server is a different
    /// registered domain than the apex — e.g. <c>droply.top</c> for <c>hexload.com</c>), with
    /// Referer, matching the browser capture. No Cookie: the anonymous POST carries no session.
    /// </summary>
    private Dictionary<string, string> BrowserAnonymousHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = Host,
        ["Referer"] = Host + "/",
        ["Sec-Fetch-Site"] = "cross-site",
        ["Sec-Fetch-Mode"] = "cors",
        ["Sec-Fetch-Dest"] = "empty",
    };

    private async Task<(string? ApiKey, bool DidBootstrap, string? Error)> EnsureApiKeyAsync(AttemptContext ctx, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(ctx.Credentials.ApiKey))
        {
            return (ctx.Credentials.ApiKey, false, null);
        }

        SemaphoreSlim gate = _bootstrapGates.GetOrAdd(ctx.Credentials.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(ctx.Credentials.ApiKey))
            {
                return (ctx.Credentials.ApiKey, false, null);
            }

            if (string.IsNullOrEmpty(ctx.Credentials.Username))
            {
                return (null, false, "no API key set and no username supplied — open Settings → Accounts and either paste an API key or sign in with username/password");
            }

            return await BootstrapApiKeyAsync(ctx, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(string? ApiKey, bool DidBootstrap, string? Error)> BootstrapApiKeyAsync(AttemptContext ctx, CancellationToken ct)
    {
        // Ungated: EnsureApiKeyAsync already holds this account's gate, and it isn't reentrant.
        string? xfss = HasValidStoredSessionCookie(ctx)
            ? ctx.Credentials.SessionCookie
            : await AcquireXfssCookieAsync(ctx, ct).ConfigureAwait(false);
        if (xfss is null)
        {
            return (null, true, "sign-in cancelled or no usable proxy available");
        }

        IReadOnlyDictionary<string, string> cookieHeader = BuildCookieHeader(xfss);
        string html;
        try
        {
            html = await GetAsync(ctx, MyAccountUrl, cookieHeader, ct);
        }
        catch (Exception ex)
        {
            return (null, true, "my_account fetch failed: " + ex.Message);
        }

        string? apiKey = ExtractApiKey(html);

        if (apiKey is null)
        {
            string? csrf = ExtractCsrfToken(html);
            if (csrf is null)
            {
                return (null, true, "my_account did not contain an API key OR a CSRF token to generate one. " + Snippet(html));
            }

            string generateUrl = $"{MyAccountUrl}&generate_api_key=1&token={Uri.EscapeDataString(csrf)}";
            try
            {
                _ = await GetAsync(ctx, generateUrl, cookieHeader, ct);
            }
            catch (Exception ex)
            {
                return (null, true, "generate_api_key request failed: " + ex.Message);
            }

            try
            {
                html = await GetAsync(ctx, MyAccountUrl, cookieHeader, ct);
            }
            catch (Exception ex)
            {
                return (null, true, "my_account re-fetch failed after generate: " + ex.Message);
            }

            apiKey = ExtractApiKey(html);
            if (apiKey is null)
            {
                return (null, true, "my_account did not contain an api-url input after generate. " + Snippet(html));
            }
        }

        await PersistApiKeyAsync(ctx.Credentials, apiKey, ct).ConfigureAwait(false);

        ctx.Logger.Log(this, LogType.Status, $"{Name}: bootstrapped API key for {ctx.Credentials.Username}");
        return (apiKey, true, null);
    }

}
