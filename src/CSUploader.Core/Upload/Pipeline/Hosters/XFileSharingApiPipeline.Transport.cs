// <copyright file="XFileSharingApiPipeline.Transport.cs" company="CSUploader">
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
/// The upload transport (classic single-multipart and chunked up.cgi/api.cgi protocols), the
/// shared HTTP/cookie plumbing, and the scrape/parse utilities, as a partial: one class across
/// five files, one concern each (see the main file's class doc).
/// </summary>
public abstract partial class XFileSharingApiPipeline
{
    /// <summary>
    /// Honours <see cref="DowngradeUploadServerToHttp"/>: when set, rewrites an <c>https://</c>
    /// upload URL whose host differs from the API host to <c>http</c>; otherwise returns the URL
    /// unchanged. A URL pointing back at the API host always stays as-given.
    /// </summary>
    private string NormaliseUploadUrlScheme(string uploadUrl)
    {
        if (!DowngradeUploadServerToHttp)
        {
            return uploadUrl;
        }

        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out Uri? uploadUri))
        {
            return uploadUrl;
        }
        if (uploadUri.Scheme != Uri.UriSchemeHttps)
        {
            return uploadUrl;
        }
        if (!Uri.TryCreate(Host, UriKind.Absolute, out Uri? apiUri))
        {
            return uploadUrl;
        }
        if (string.Equals(uploadUri.Host, apiUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return uploadUrl;
        }
        UriBuilder b = new(uploadUri) { Scheme = Uri.UriSchemeHttp };
        // UriBuilder defaults the port to the new scheme's default (80) only when the
        // original URL didn't carry an explicit port — that's exactly the behaviour
        // we want here. If the API ever returns an explicit port we preserve it.
        if (uploadUri.IsDefaultPort)
        {
            b.Port = -1;
        }
        return b.Uri.ToString();
    }

    /// <summary>
    /// Browser-shaped headers for the classic single-multipart <c>upload.cgi</c> POST.
    /// Sec-Fetch-Site is <c>same-site</c> because classic XFileSharing keeps the upload
    /// on a subdomain of the apex (e.g. <c>fs40.ex-load.com</c>) — the BRupload-era
    /// shape that proven-working hosters expect.
    /// </summary>
    private Dictionary<string, string> BrowserClassicHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = Host,
        ["Sec-Fetch-Site"] = "same-site",
        ["Sec-Fetch-Mode"] = "cors",
        ["Sec-Fetch-Dest"] = "empty",
    };

    /// <summary>
    /// Browser-shaped headers for the chunked <c>up.cgi</c> / <c>api.cgi</c> POSTs.
    /// Sec-Fetch-Site is <c>cross-site</c> because the modern XFileSharing CDN backends
    /// live on a different registered domain than the apex (e.g. <c>ctmp.world</c> for
    /// hxfile.co). Referer is included to match the browser capture; some XFS CDN
    /// fronts reject preflight-less POSTs without it.
    /// </summary>
    private Dictionary<string, string> BrowserChunkedHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = Host,
        ["Sec-Fetch-Site"] = "cross-site",
        ["Sec-Fetch-Mode"] = "cors",
        ["Sec-Fetch-Dest"] = "empty",
        ["Referer"] = Host + "/",
    };

    /// <summary>
    /// Initial chunk size for the modern XFileSharing chunked protocol. 80 MiB is hard-
    /// coded in the upload-chunked.js loaded by hxfile.co (and is what their CDN
    /// frontends expect). We start here for maximum throughput; if chunk 0 returns 413
    /// we shrink to <see cref="ChunkedUploadFallbackChunkSize"/> and retry once.
    /// </summary>
    private const int ChunkedUploadInitialChunkSize = 80 * 1024 * 1024;

    /// <summary>
    /// Fallback chunk size used after a chunk-0 413 from the storage backend. 20 MiB
    /// sits comfortably under the IIS default <c>maxAllowedContentLength</c> of
    /// ~28.6 MiB (FlashBit's storage tier is Microsoft-IIS/10.0, observed 2026-06-03).
    /// If 20 MiB also gets 413 we give up on chunked and fall back to classic — a third
    /// tier of guesses would just delay the inevitable.
    /// </summary>
    private const int ChunkedUploadFallbackChunkSize = 20 * 1024 * 1024;

    /// <summary>
    /// Upload router: dispatches to the chunked or classic protocol based on the
    /// subclass's <see cref="UsesChunkedUpload"/> declaration. No probe-then-fallback —
    /// the declaration is the single source of truth, so misdeclarations fail fast
    /// (visible AttemptFailed with the server's actual response) and the user pays
    /// zero wasted bytes for a probe that just confirms what we already know.
    /// </summary>
    /// <remarks>
    /// On chunked success the api.cgi XML response is normalised into the classic
    /// <c>[{file_code, file_status:"OK"}]</c> JSON shape so the existing
    /// <see cref="ParseUploadResponse"/> works unchanged for both code paths.
    /// </remarks>
    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string uploadUrl, string sessId)
    {
        // Test override path stays on the classic shape (it's how the existing tests are
        // wired). Only the production path goes through the router.
        if (_uploadOverride is not null)
        {
            return await _uploadOverride(
                ctx.FilePath,
                uploadUrl,
                BuildClassicExtraFields(ctx, sessId),
                BrowserClassicHeaders(),
                ctx.SpeedBudget);
        }

        if (UsesChunkedUpload)
        {
            // Subclass declared chunked but the hoster's up.cgi rejected the probe.
            // No fallback — fail loudly so the misdeclaration gets fixed at its source
            // (override UsesChunkedUpload to false) instead of silently masking the
            // real protocol with classic.
            HttpResponseSnapshot? chunkedResult = await TryChunkedUploadAsync(ctx, uploadUrl, sessId) ?? throw new InvalidOperationException(
                $"{Name}: declared UsesChunkedUpload=true but up.cgi did not accept chunk 0. "
                + $"Either the hoster removed chunked support (override UsesChunkedUpload to false) "
                + $"or the API-supplied upload URL ({uploadUrl}) isn't a chunked endpoint.");
            return chunkedResult;
        }

        return await ClassicUploadAsync(ctx, uploadUrl, sessId);
    }

    /// <summary>
    /// Classic XFileSharing upload — one giant <c>multipart/form-data</c> POST to the URL
    /// the API handed us. Browser-shaped per <c>brupload-multipart-quirks</c>.
    /// </summary>
    private Task<HttpResponseSnapshot> ClassicUploadAsync(AttemptContext ctx, string uploadUrl, string sessId)
        => ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            uploadUrl,
            fileFieldName: "file_0",
            extraFields: BuildClassicExtraFields(ctx, sessId),
            headers: BrowserClassicHeaders(),
            speedBudget: ctx.SpeedBudget,
            cancellationToken: ctx.Cancellation);

    /// <summary>
    /// Attempt-aware form of <see cref="BuildClassicExtraFields(string)"/>, and the one the upload
    /// actually calls. Default: defers to the <c>sessId</c>-only overload, so every existing fork is
    /// unaffected.
    /// <para>
    /// Exists because a pipeline is a SINGLETON. A fork whose field set carries values scraped from
    /// the upload page — subyshare.com posts the account's <c>usr_id</c> and the node's
    /// <c>srv_tmp_url</c> — has nowhere to put them between
    /// <see cref="ResolveWebFormUploadServerAsync"/> and here except instance state, and two
    /// concurrent uploads would then overwrite each other's: the wrong <c>usr_id</c> files someone
    /// else's upload under this account, and XFileSharing does not complain about a field it merely
    /// disagrees with. Keying that state by <see cref="AttemptContext.AttemptId"/> is what makes it
    /// safe, and this overload is what supplies the id.
    /// </para>
    /// </summary>
    protected virtual Dictionary<string, string> BuildClassicExtraFields(AttemptContext ctx, string sessId)
    {
        _ = ctx;
        return BuildClassicExtraFields(sessId);
    }

    /// <summary>
    /// Field set the browser posts alongside the file part for a classic logged-in upload.
    /// <c>protected virtual</c> so web-form hosters whose live capture shows a different set can
    /// override it (isra.cloud sends an empty <c>file_public</c> and no <c>upload</c> button) —
    /// the XFileSharing multipart parser is field-presence/value sensitive (see
    /// <c>brupload-multipart-quirks</c>), so each hoster replicates its own proven set rather than
    /// risk a wasted upload on a near-miss.
    /// </summary>
    protected virtual Dictionary<string, string> BuildClassicExtraFields(string sessId) => new(StringComparer.Ordinal)
    {
        ["sess_id"] = sessId,
        ["utype"] = "reg",
        ["file_descr"] = string.Empty,
        ["file_public"] = "1",
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
        ["upload"] = "Start upload",
        ["keepalive"] = "1",
    };

    /// <summary>
    /// Modern XFileSharing chunked upload (verified against hxfile.co's
    /// <c>upload-chunked.js</c> + Fiddler trace on 2026-06-01):
    /// </summary>
    /// <returns>
    /// On chunked success, a synthesised <see cref="HttpResponseSnapshot"/> whose body is
    /// the classic JSON shape so the caller can <see cref="ParseUploadResponse"/> it.
    /// <c>null</c> means the hoster doesn't support the chunked endpoint and the caller
    /// should fall back to classic. Any other failure throws.
    /// </returns>
    private async Task<HttpResponseSnapshot?> TryChunkedUploadAsync(AttemptContext ctx, string uploadUrl, string sessId)
    {
        if (!TryDeriveChunkedEndpoints(uploadUrl, out string upCgiUrl, out string apiCgiUrl))
        {
            // URL doesn't end with "upload.cgi" — classic path can't be derived from it
            // either, but we have no chunked endpoint to try. Surface as fallback so
            // ClassicUploadAsync at least tries the URL verbatim.
            return null;
        }

        string clientSid = GenerateChunkSessionId();
        string fileName = Path.GetFileName(ctx.FilePath);
        Dictionary<string, string> headers = BrowserChunkedHeaders();
        DateTime started = DateTime.Now;

        await using FileStream file = new(ctx.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileSize = file.Length;
        long position = 0;
        int chunkIndex = 0;
        int currentChunkSize = ChunkedUploadInitialChunkSize;
        bool shrinkAttempted = false;

        while (position < fileSize)
        {
            long thisChunkLen = Math.Min(currentChunkSize, fileSize - position);
            ChunkSliceStream slice = new(file, thisChunkLen);

            HttpResponseSnapshot chunkResp;
            try
            {
                chunkResp = await ctx.Handler.PostChunkAsync(
                    endpoint: upCgiUrl,
                    sid: clientSid,
                    chunkData: slice,
                    chunkLength: thisChunkLen,
                    chunkIndex: chunkIndex,
                    basePosition: position,
                    totalFileSize: fileSize,
                    dateTimeStarted: started,
                    headers: headers,
                    speedBudget: ctx.SpeedBudget,
                    cancellationToken: ctx.Cancellation);
            }
            catch when (chunkIndex == 0)
            {
                // First-chunk transport failure (DNS, refused, TLS) → tentatively chunked-
                // not-supported. Caller falls back to classic which hits the URL verbatim.
                return null;
            }

            // Chunk-0 413 → endpoint exists but rejects our chunk size. Probe-and-shrink:
            // retry chunk 0 once at the smaller fallback size. Storage backends with
            // tight IIS defaults (FlashBit: Microsoft-IIS/10.0, ~28.6 MiB cap, observed
            // 2026-06-03) accept the 20 MiB fallback while still letting hxfile-style
            // CDN frontends use the full 80 MiB on the first try. Rewind the file stream
            // and rotate the sid so any server-side state from the rejected attempt
            // doesn't poison the retry.
            if (chunkIndex == 0 && chunkResp.StatusCode == 413 && !shrinkAttempted)
            {
                ctx.Logger.Log(
                    this,
                    LogType.Status,
                    $"{Name}: chunked up.cgi rejected the {currentChunkSize / (1024 * 1024)} MiB "
                    + $"first chunk with HTTP 413 — retrying at {ChunkedUploadFallbackChunkSize / (1024 * 1024)} MiB.");
                shrinkAttempted = true;
                currentChunkSize = ChunkedUploadFallbackChunkSize;
                file.Position = 0;
                clientSid = GenerateChunkSessionId();
                continue;
            }

            // Chunk-0 fallback gate: ANY non-2xx response on the first chunk drops to
            // classic (or, after a shrink attempt, a second 413 also drops here).
            // Reasons we've actually observed in the wild:
            //   • 404 / 410 / 405 — up.cgi doesn't exist on the storage backend.
            //   • 413 (after shrink) — even the fallback chunk size is too big; give up
            //     on chunked and let the classic path try the original URL.
            //   • Other 4xx (411, 400) — endpoint disagrees with our request shape;
            //     falling back is cheaper than throwing.
            // Later-chunk failures still throw — retrying classic against a partially
            // populated server-side sid would waste the bytes already uploaded.
            if (chunkIndex == 0 && chunkResp.StatusCode is < 200 or >= 300)
            {
                ctx.Logger.Log(
                    this,
                    LogType.Status,
                    $"{Name}: chunked up.cgi rejected chunk 0 with HTTP {chunkResp.StatusCode} "
                    + $"({ChunkSnippet(chunkResp.Body)}) — falling back to classic single-multipart upload.");
                return null;
            }

            if (chunkResp.StatusCode is < 200 or >= 300)
            {
                throw new InvalidOperationException(
                    $"chunked upload: chunk {chunkIndex} returned HTTP {chunkResp.StatusCode} (body: {ChunkSnippet(chunkResp.Body)})");
            }

            if (!ChunkResponseIsOk(chunkResp.Body))
            {
                if (chunkIndex == 0)
                {
                    // Unexpected non-<OK> body on chunk 0 — same diagnosis as a 4xx: this
                    // backend doesn't speak the chunked protocol we expect. Fall back.
                    ctx.Logger.Log(
                        this,
                        LogType.Status,
                        $"{Name}: chunked up.cgi returned unexpected body on chunk 0 "
                        + $"({ChunkSnippet(chunkResp.Body)}) — falling back to classic single-multipart upload.");
                    return null;
                }
                throw new InvalidOperationException(
                    $"chunked upload: chunk {chunkIndex} returned unexpected body: {ChunkSnippet(chunkResp.Body)}");
            }

            position += thisChunkLen;
            chunkIndex++;
        }

        // Finalize.
        Dictionary<string, string> finalizeFields = new(StringComparer.Ordinal)
        {
            ["op"] = "compile",
            ["sid"] = clientSid,
            ["fname"] = fileName,
            ["session_id"] = sessId,
        };
        HttpResponseSnapshot finalizeResp;
        try
        {
            finalizeResp = await PostFormWithHeadersAsync(ctx, apiCgiUrl, finalizeFields, headers);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("chunked upload: api.cgi finalize request failed: " + ex.Message, ex);
        }

        if (finalizeResp.StatusCode is < 200 or >= 300)
        {
            throw new InvalidOperationException(
                $"chunked upload: api.cgi returned HTTP {finalizeResp.StatusCode} (body: {ChunkSnippet(finalizeResp.Body)})");
        }

        string? fileCode = ParseFinalizeFileCode(finalizeResp.Body);
        if (string.IsNullOrEmpty(fileCode))
        {
            throw new InvalidOperationException(
                $"chunked upload: api.cgi returned 200 but no <Code> in response: {ChunkSnippet(finalizeResp.Body)}");
        }

        // Synthesise the classic-shape JSON so ParseUploadResponse handles both paths.
        string syntheticBody = $"[{{\"file_code\":\"{fileCode}\",\"file_status\":\"OK\"}}]";
        return new HttpResponseSnapshot(200, syntheticBody, finalizeResp.SetCookies);
    }

    /// <summary>
    /// Posts a form-urlencoded body to <paramref name="url"/> with the given browser-
    /// shape headers. Routes through the override when tests have wired one (treats the
    /// finalize call as a "tiny upload" with no file part).
    /// </summary>
    private async Task<HttpResponseSnapshot> PostFormWithHeadersAsync(
        AttemptContext ctx,
        string url,
        IReadOnlyDictionary<string, string> form,
        IReadOnlyDictionary<string, string> headers)
    {
        // PostFormAsync currently doesn't accept extra headers — fold them in via the
        // standard test override slot (form encoded as fields, no file).
        if (_uploadOverride is not null)
        {
            // Override delegate is positional (no parameter names) — pass arguments in
            // order: filePath, endpoint, extraFields, headers, speedBudget.
            return await _uploadOverride(string.Empty, url, form, headers, null);
        }
        return await ctx.Handler.PostFormAsync(url, form, ctx.Cancellation);
    }

    /// <summary>
    /// Splits the API-returned upload URL into the <c>up.cgi</c> and <c>api.cgi</c>
    /// endpoints used by the chunked protocol. The browser does this by stripping
    /// <c>upload.cgi</c> off the form action and concatenating <c>up.cgi</c> / <c>api.cgi</c>;
    /// we do the same, preserving any query string (some hosters tack
    /// <c>?upload_type=file&amp;utype=reg</c> onto the URL).
    /// </summary>
    internal static bool TryDeriveChunkedEndpoints(string uploadUrl, out string upCgiUrl, out string apiCgiUrl)
    {
        upCgiUrl = string.Empty;
        apiCgiUrl = string.Empty;
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }
        string path = uri.AbsolutePath;
        const string suffix = "upload.cgi";
        int suffixAt = path.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        if (suffixAt < 0 || suffixAt + suffix.Length != path.Length)
        {
            return false;
        }
        string basePath = path[..suffixAt];
        UriBuilder upBuilder = new(uri) { Path = basePath + "up.cgi" };
        UriBuilder apiBuilder = new(uri) { Path = basePath + "api.cgi" };
        upCgiUrl = upBuilder.Uri.ToString();
        apiCgiUrl = apiBuilder.Uri.ToString();
        return true;
    }

    /// <summary>
    /// Per-upload session id used as the <c>sid</c> field across all chunks. The browser
    /// generates this client-side as a numeric string; the server treats it opaquely as
    /// long as it's stable within one upload. We use a 12-digit decimal string seeded
    /// from a 48-bit random source — wide enough that two concurrent uploads on the same
    /// account effectively never collide.
    /// </summary>
    private static string GenerateChunkSessionId()
    {
        byte[] buf = new byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        long n = 0;
        foreach (byte b in buf)
        {
            n = (n << 8) | b;
        }

        n &= 0xFFFFFFFFFFFFL; // 48 bits → up to ~2.8e14, 12-15 decimal digits typically.
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Per-chunk acknowledgement is the literal string <c>&lt;OK&gt;</c>. Some XFS
    /// deployments wrap it in surrounding whitespace; accept that loosely.
    /// </summary>
    internal static bool ChunkResponseIsOk(string body)
        => body.Trim().StartsWith("<OK>", StringComparison.Ordinal);

    /// <summary>
    /// Pulls the file_code out of the finalize XML. The browser path expects
    /// <c>&lt;Links&gt;&lt;Code&gt;…&lt;/Code&gt;…&lt;/Links&gt;</c>; we also accept the
    /// older XML shape that some deployments use (<c>&lt;root&gt;&lt;Code&gt;…</c>) by
    /// regexing for <c>&lt;Code&gt;</c> directly. Returns null if no code is present —
    /// the caller treats that as a finalize failure.
    /// </summary>
    internal static string? ParseFinalizeFileCode(string xml)
    {
        Match m = _finalizeCodeRegex.Match(xml);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static readonly Regex _finalizeCodeRegex = new(
        @"<Code>\s*([A-Za-z0-9]+)\s*</Code>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string ChunkSnippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty)";
        }

        string s = body.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    private Dictionary<string, string> BuildCookieHeader(string session)
        // A combined cf_clearance-mode session is already a full "name=value; name=value" Cookie
        // header (it contains '='); forward it verbatim. A classic session is a bare xfss token
        // (alphanumeric, never '=') that we wrap. The '=' test cleanly distinguishes the two.
        => new(StringComparer.Ordinal)
        {
            ["Cookie"] = session.Contains('=', StringComparison.Ordinal)
                ? session
                : CookieName + "=" + session,
        };

    /// <summary>
    /// Last chance to turn a fork's upload reply into the family's <c>[{file_code, file_status}]</c>
    /// envelope before <c>ParseUploadResponse</c> reads it. Default: unchanged.
    /// <para>
    /// Exists because the envelope varies while the MEANING does not. Older XFileSharing builds answer
    /// upload.cgi with a self-submitting HTML form carrying <c>fn</c>/<c>st</c> textareas rather than
    /// JSON (subyshare.com is one), and the chunked path already synthesises the same shape after its
    /// finalise call. Translating here rather than exposing the parser keeps ONE place that knows what
    /// "success" means — including that <c>file_code:"undef"</c> is a discarded upload, which is
    /// exactly the trap a fork-specific parser would be written without.
    /// </para>
    /// </summary>
    protected virtual HttpResponseSnapshot NormalizeUploadResponse(HttpResponseSnapshot response) => response;

    private (string? Url, string? Error, bool AuthExpired) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"upload.cgi failed (HTTP {response.StatusCode}): {Snippet(response.Body)}", false);
        }

        UploadResult[]? results;
        try
        {
            results = JsonSerializer.Deserialize<UploadResult[]>(response.Body);
        }
        catch
        {
            results = null;
        }

        if (results is null || results.Length == 0)
        {
            return (null, $"upload.cgi: response was not the expected JSON array: {Snippet(response.Body)}", false);
        }

        UploadResult first = results[0];
        if (string.Equals(first.Status, "Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, true);
        }

        if (!string.Equals(first.Status, "OK", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"upload.cgi: file_status={first.Status ?? "(null)"}", false);
        }

        // "undef" is a code the family prints when it DISCARDED the upload — DataVaults answers an
        // unauthenticated post with a cheerful [{"file_status":"OK","file_code":"undef"}], and its
        // siblings pair the same placeholder with a refusal in file_status (caught above). Treated as
        // empty, because the alternative is handing the user https://host/undef and calling it a
        // success: a dead link reported as a finished upload is the worst failure this app can have.
        if (string.IsNullOrEmpty(first.Code) || string.Equals(first.Code, "undef", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"upload.cgi: file_status=OK but file_code was {(string.IsNullOrEmpty(first.Code) ? "empty" : "\"undef\"")} — the server accepted the request but stored nothing (usually an unauthenticated or out-of-quota upload)", false);
        }

        // The server is the authority on its own URL form. When it names a domain, use it: EliteFile
        // stores files on elfile.net while the site is elitefile.net, and building the link from Host
        // would hand the user a different domain than the host's own result page shows.
        string prefix = first.Domain is { Length: > 0 } domain
            ? domain.TrimEnd('/') + "/"
            : PublicUrlPrefix;
        return (prefix + first.Code, null, false);
    }

    private static string? ExtractApiKey(string html)
    {
        Match m = _apiKeyRegex.Match(html);
        if (m.Success)
        {
            // One of four groups captures depending on which branch matched (see the regex
            // definition for the four shapes). Pick the non-empty one.
            for (int i = 1; i <= 4; i++)
            {
                if (m.Groups[i].Success && m.Groups[i].Length > 0)
                {
                    return m.Groups[i].Value;
                }
            }
        }

        // Fall back to the bare-token shape (Hxfile): a raw key next to the regenerate link,
        // with no api-url URL to parse. Only reached when none of the four URL shapes matched.
        Match bare = _apiKeyBareTokenRegex.Match(html);
        return bare.Success && bare.Groups[1].Length > 0 ? bare.Groups[1].Value : null;
    }

    private static string? ExtractCsrfToken(string html)
    {
        Match m = _csrfTokenRegex.Match(html);
        if (!m.Success)
        {
            return null;
        }

        string captured = m.Groups[1].Success && m.Groups[1].Length > 0
            ? m.Groups[1].Value
            : m.Groups[2].Value;
        return string.IsNullOrEmpty(captured) ? null : captured;
    }

    /// <summary>Trimmed, single-line excerpt of a response body for error messages. <c>protected</c>
    /// so a subclass overriding a discovery step can describe an unexpected response the same way.</summary>
    protected static string Snippet(string body)
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

    /// <summary>
    /// Builds the verbose failure detail for <see cref="AccountCheckResult.Detail"/>: the short
    /// human summary followed by the complete, untruncated response body (unlike <see cref="Snippet"/>,
    /// which caps at 200 chars for inline status text). The Add Account "Details" dialog renders
    /// this verbatim, so the body keeps its original line breaks. Falls back to just the summary
    /// when the body is empty.
    /// </summary>
    private static string BuildFailureDetail(string summary, string responseBody)
        => string.IsNullOrWhiteSpace(responseBody)
            ? summary
            : summary + Environment.NewLine + Environment.NewLine + responseBody;

    /// <summary>Fetches a page/endpoint through the pipeline's own (test-overridable) GET, following
    /// redirects manually. <c>protected</c> so a subclass overriding a discovery step keeps using the
    /// same seam the test ctor stubs.</summary>
    protected async Task<string> GetAsync(AttemptContext ctx, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        if (_getOverride is not null)
        {
            return await _getOverride(url, headers);
        }

        // Production: follow redirects manually. The global HttpHandler runs with
        // AllowAutoRedirect=false (BRupload's login branches on 302), so without this
        // ex-load.com's first /?op=my_account hit (which 302s when the session jar is
        // missing companion cookies like `lang`) lands us on a sub-200-byte stub instead
        // of the logged-in HTML, breaking ApiKey extraction.
        (string body, string _, int _) = await FetchFollowingRedirectsAsync(
            url,
            headers,
            (u, h, t) => ctx.Handler.GetSnapshotAsync(u, h, t),
            ct).ConfigureAwait(false);
        return body;
    }

    /// <summary>
    /// CheckAccountAsync's my_account fetch (also reused for the post-generate refetch and
    /// the generate_api_key side-call). When the test override is set we keep the existing
    /// no-redirect semantics so canned-HTML fixtures don't need rewriting; in production
    /// we drive through <see cref="FetchFollowingRedirectsAsync"/> to dodge the
    /// 302-on-first-hit problem ex-load.com exhibits. Returns the final body, the URL we
    /// last hit, and the hop count so the caller can include a useful diagnostic when
    /// extraction fails.
    /// </summary>
    private async Task<(string Body, string FinalUrl, int Hops)> FetchMyAccountAsync(
        HttpHandler handler, string url, IReadOnlyDictionary<string, string> cookieHeader, CancellationToken ct)
    {
        if (_getOverride is not null)
        {
            return (await _getOverride(url, cookieHeader), url, 0);
        }

        return await FetchFollowingRedirectsAsync(
            url,
            cookieHeader,
            (u, h, t) => handler.GetSnapshotAsync(u, h, t),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// GETs <paramref name="url"/> and follows 3xx redirects (resolving relative Location
    /// targets against the previous URL), bounded by <paramref name="maxHops"/> — which
    /// is the TOTAL request budget, NOT redirects-after-the-initial. Returns the final
    /// body, the URL we last hit, and the redirect count taken. Static so callers can
    /// stub via the snapshot factory in tests without needing a real HttpHandler. Stops
    /// on the first non-redirect response, on a redirect with no usable Location, or
    /// when the request budget is exhausted (in which case Hops == maxHops and Body is
    /// the last 3xx body for diagnostics).
    /// </summary>
    internal static async Task<(string Body, string FinalUrl, int Hops)> FetchFollowingRedirectsAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers,
        Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<HttpResponseSnapshot>> get,
        CancellationToken ct,
        int maxHops = 5)
    {
        string current = url;
        HttpResponseSnapshot? lastSnap = null;

        // Cookie jar accumulated across redirect hops. ex-load.com's first /?op=my_account
        // hit responds 302 + Set-Cookie: lang=english and redirects back to the SAME URL,
        // expecting the freshly-set cookie on the follow-up (confirmed via browser capture).
        // A plain re-request with the original header alone never sends `lang`, so the
        // server keeps returning a degraded page with no api-url. Seed the jar from the
        // caller's Cookie header, then merge each hop's Set-Cookie — exactly what a browser
        // does — and rebuild the header for the next request.
        Dictionary<string, string> cookieJar = ParseCookieHeader(headers);
        IReadOnlyDictionary<string, string>? currentHeaders = headers;

        for (int attempt = 0; attempt < maxHops; attempt++)
        {
            lastSnap = await get(current, currentHeaders, ct).ConfigureAwait(false);
            bool isRedirect = lastSnap.StatusCode is >= 300 and < 400 && !string.IsNullOrEmpty(lastSnap.LocationHeader);

            // Merge any Set-Cookie values this hop returned into the jar so they ride the
            // next request. Applies on non-redirects too (harmless), but only matters on 3xx.
            if (MergeSetCookies(cookieJar, lastSnap.SetCookies) && headers is not null)
            {
                currentHeaders = RebuildHeadersWithCookies(headers, cookieJar);
            }

            if (!isRedirect)
            {
                // attempt == number of redirects actually followed (0 on a straight 200).
                return (lastSnap.Body, current, attempt);
            }

            // Resolve Location against the current URL so relative paths work
            // (XFS hosters frequently emit "Location: /?op=login" with no scheme).
            current = new Uri(new Uri(current), lastSnap.LocationHeader!).AbsoluteUri;
        }

        // Request budget exhausted — every call within the budget came back 3xx. Return
        // the LAST 3xx body so the caller's diagnostic reflects "we kept getting bounced",
        // and `current` reflects the URL we would have tried next.
        return (lastSnap?.Body ?? string.Empty, current, maxHops);
    }

    /// <summary>Parses a <c>Cookie</c> request header value ("a=1; b=2") into a name→value
    /// map. Returns an empty map when the header dict has no Cookie entry.</summary>
    private static Dictionary<string, string> ParseCookieHeader(IReadOnlyDictionary<string, string>? headers)
    {
        Dictionary<string, string> jar = [with(StringComparer.Ordinal)];
        if (headers is null || !headers.TryGetValue("Cookie", out string? cookie) || string.IsNullOrEmpty(cookie))
        {
            return jar;
        }

        foreach (string pair in cookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                jar[pair[..eq]] = pair[(eq + 1)..];
            }
        }

        return jar;
    }

    /// <summary>Merges raw <c>Set-Cookie</c> header values (each "name=value; Path=/; …")
    /// into <paramref name="jar"/>, keeping only the name=value before the first ';'.
    /// Returns true when at least one cookie was added or changed.</summary>
    private static bool MergeSetCookies(Dictionary<string, string> jar, IReadOnlyList<string> setCookies)
    {
        bool changed = false;
        foreach (string raw in setCookies)
        {
            int semi = raw.IndexOf(';', StringComparison.Ordinal);
            string nameValue = (semi < 0 ? raw : raw[..semi]).Trim();
            int eq = nameValue.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string name = nameValue[..eq];
            string value = nameValue[(eq + 1)..];
            if (!jar.TryGetValue(name, out string? existing) || !string.Equals(existing, value, StringComparison.Ordinal))
            {
                jar[name] = value;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Clones <paramref name="baseHeaders"/> and replaces the <c>Cookie</c> entry
    /// with one serialized from <paramref name="jar"/> ("a=1; b=2"), preserving every
    /// other header the caller set (Origin, etc.).</summary>
    private static Dictionary<string, string> RebuildHeadersWithCookies(IReadOnlyDictionary<string, string> baseHeaders, Dictionary<string, string> jar)
    {
        Dictionary<string, string> rebuilt = new(baseHeaders, StringComparer.Ordinal)
        {
            ["Cookie"] = string.Join("; ", jar.Select(kv => kv.Key + "=" + kv.Value))
        };
        return rebuilt;
    }

    private async Task<AccountInfo?> TryGetAccountInfoAsync(string apiKey, HttpHandler handler, CancellationToken ct)
    {
        string url = $"{ApiAccountInfoUrl}?key={Uri.EscapeDataString(apiKey)}";
        string body;
        try
        {
            body = _getOverride is not null
                ? await _getOverride(url, null)
                : await handler.GetStringAsync(url, ct);
        }
        catch
        {
            return null;
        }

        try
        {
            AccountInfoResponse? response = JsonSerializer.Deserialize<AccountInfoResponse>(body);
            return response is null || response.Status != 200 ? null : response.Result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Maps account/info's premium-expire string into the app's AccountType
    /// taxonomy. Returns Premium when the expiry is in the future, Free otherwise.</summary>
    private static (AccountType Type, DateTime? Expiry) ClassifyPremium(AccountInfo info)
    {
        if (string.IsNullOrEmpty(info.PremiumExpire))
        {
            return (AccountType.Free, null);
        }

        if (!DateTime.TryParse(info.PremiumExpire, System.Globalization.CultureInfo.InvariantCulture, out DateTime expiry))
        {
            return (AccountType.Free, null);
        }

        return expiry > DateTime.UtcNow
            ? (AccountType.Premium, expiry)
            : (AccountType.Free, expiry);
    }

    /// <summary>
    /// Extracts storage usage from the <c>/api/account/info</c> result. Both
    /// <c>storage_used</c> and <c>storage_left</c> arrive as EITHER a JSON string or a JSON
    /// number depending on the hoster — ex-load renders <c>storage_used:"415593052"</c> /
    /// <c>storage_left:"inf"</c> (strings) while KatFile renders
    /// <c>storage_used:"991247477"</c> / <c>storage_left:2198032008075</c> (number). The
    /// fields are typed <see cref="JsonElement"/> so deserialization tolerates both shapes.
    /// Returns (used, quota) where quota = used + left when left is a real number, or null
    /// when left is <c>"inf"</c>/missing/unparseable (the grid's Available cell then renders
    /// "Unlimited"). Used is null only when its field is absent or unparseable.
    /// </summary>
    private static (long? Used, long? Quota) ParseStorageFromAccountInfo(AccountInfo info)
    {
        long? used = TryReadStorageLong(info.StorageUsed);

        // Hexload reports an EMPTY account's storage_used as JSON null (its own dashboard shows
        // "0.00 GB") rather than "0". System.Text.Json maps a JSON null into a JsonElement?
        // property as C# null — indistinguishable from the field being absent — so use
        // storage_left's PRESENCE as the signal that the response carried storage info at all:
        // when storage_left is present but storage_used didn't parse, the account is simply
        // empty → 0 used. Older XFS hosters that omit BOTH fields leave used null/blank.
        if (used is null && info.StorageLeft is not null)
        {
            used = 0L;
        }

        long? quota = null;
        if (used is long usedBytes && TryReadStorageLong(info.StorageLeft) is long left)
        {
            // Real numeric left → cap. "inf" / non-numeric / absent → unlimited (quota null).
            quota = usedBytes + left;
        }

        return (used, quota);
    }

    /// <summary>Reads a byte count out of a storage field that may be a JSON string
    /// (e.g. <c>"991247477"</c>) or a JSON number (e.g. <c>2198032008075</c>). Returns null
    /// for absent fields, the literal <c>"inf"</c>, or anything non-numeric.</summary>
    private static long? TryReadStorageLong(JsonElement? element)
    {
        if (element is not JsonElement e)
        {
            return null;
        }

        return e.ValueKind switch
        {
            JsonValueKind.Number when e.TryGetInt64(out long n) => n,
            JsonValueKind.String when long.TryParse(e.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long s) => s,
            _ => null,
        };
    }

    private async Task PersistApiKeyAsync(FileHosterLoginDto credentials, string apiKey, CancellationToken ct)
    {
        credentials.ApiKey = apiKey;
        credentials.SessionCookie = null;
        credentials.SessionCookieExpiresUtc = null;
        credentials.PinnedProxyId = null;

        if (_loginRepository is null)
        {
            return;
        }

        await _loginRepository.UpdateAsync(credentials, ct).ConfigureAwait(false);
    }

    private async Task ClearApiKeyAsync(FileHosterLoginDto credentials, CancellationToken ct)
    {
        credentials.ApiKey = null;

        if (_loginRepository is null)
        {
            return;
        }

        await _loginRepository.UpdateAsync(credentials, ct).ConfigureAwait(false);
    }

    // ---- JSON wire types ----

    private sealed class AccountInfoResponse
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        [JsonPropertyName("result")] public AccountInfo? Result { get; set; }
    }

    private sealed class AccountInfo
    {
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("premium_expire")] public string? PremiumExpire { get; set; }
        [JsonPropertyName("balance")] public string? Balance { get; set; }

        /// <summary>Bytes currently consumed. Arrives as a JSON string (ex-load,
        /// "415593052") OR a JSON number depending on the hoster — typed
        /// <see cref="JsonElement"/> so deserialization accepts either. Parsed via
        /// <c>TryReadStorageLong</c>.</summary>
        [JsonPropertyName("storage_used")] public JsonElement? StorageUsed { get; set; }

        /// <summary>Remaining storage. A byte count rendered as EITHER a JSON string or a
        /// JSON number (KatFile: <c>2198032008075</c>), or the literal string <c>"inf"</c>
        /// for unlimited (ex-load). Typed <see cref="JsonElement"/> so deserialization
        /// tolerates all three; "inf"/non-numeric → quota null → grid shows "Unlimited".</summary>
        [JsonPropertyName("storage_left")] public JsonElement? StorageLeft { get; set; }
    }

    private sealed class UploadServerResponse
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        [JsonPropertyName("sess_id")] public string? SessId { get; set; }
        [JsonPropertyName("result")] public string? Result { get; set; }
    }

    private sealed class UploadResult
    {
        [JsonPropertyName("file_code")] public string? Code { get; set; }
        [JsonPropertyName("file_status")] public string? Status { get; set; }

        /// <summary>
        /// Some forks answer with the domain the file actually lives on, which is NOT always the site
        /// you uploaded to — EliteFile posts to elitefile.net and replies
        /// <c>{"domain":"https://elfile.net",…}</c>, and its own result page links elfile.net. Honoured
        /// when present (see <see cref="ParseUploadResponse"/>); absent on every other host so far.
        /// </summary>
        [JsonPropertyName("domain")] public string? Domain { get; set; }
    }

    [GeneratedRegex("""name=["']token["'][^>]*?value=["']([^"']*)["']|value=["']([^"']*)["'][^>]*?name=["']token["']""", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
    private static partial Regex MyRegex();
}
