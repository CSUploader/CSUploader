// <copyright file="SendNowPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Send.now — classic XFileSharing; the protocol lives in <see cref="XFileSharingApiPipeline"/>.
/// <para>
/// Formerly <b>send.cm</b> (and tusfiles / sendit before that): send.cm now 301s to send.now, so the
/// live brand is the one wired here — a single entry covers traffic addressed to either.
/// </para>
/// <para>
/// It is genuinely stock XFS — <c>?op=api_get_limits</c> answers with the standard
/// <c>&lt;Data&gt;…&lt;ServerURL&gt;…</c> XML — and the upload itself is the family's ordinary anonymous
/// POST (empty <c>sess_id</c>, <c>utype=anon</c>, <c>file_0</c>, answered with
/// <c>[{"file_status":"OK","file_code":…}]</c>), confirmed against a live browser capture 2026-07-26.
/// </para>
/// <para>
/// <b>Cloudflare shapes how this hoster must be driven.</b> Its HTML pages sit behind a <i>managed</i>
/// challenge: a real run got <c>403</c> + <c>Cf-Mitigated: challenge</c> + <c>cType:'managed'</c> merely
/// for fetching the homepage, while the JSON API calls from the same client were served normally. So
/// this pipeline resolves the upload node from <c>/api/upload/server</c> and never scrapes a page —
/// see <see cref="DiscoverAnonymousServerAsync"/>.
/// </para>
/// <para>
/// Known risk for the ACCOUNT path (untested — no account has been used yet): the base's
/// <c>CheckAccountAsync</c> scrapes <c>?op=my_account</c> with the C# handler to extract the API key,
/// and that is an HTML page on the challenged domain. The upload itself is safe (it uses the same
/// <c>/api/upload/server</c> endpoint), so if sign-in fails with a challenge, the fix is to source the
/// key without touching an HTML page — not to give up on the hoster.
/// </para>
/// </summary>
public sealed class SendNowPipeline : XFileSharingApiPipeline
{
    public SendNowPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow
    /// from canned responses.</summary>
    internal SendNowPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Send.now";

    protected override string Host => "https://send.now";

    /// <summary>Anonymous (not-logged-in) upload verified against the live homepage form.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>Guest (anonymous) per-file cap — 100 GB, the figure the site states. Decimal, not
    /// binary: the exact byte boundary behind a "100GB" claim is unstated, and of the two ways to be
    /// wrong, rejecting a 100-107 GB file early costs nothing while accepting one the server then
    /// refuses would waste an enormous upload (the Upstore lesson).</summary>
    private const long GuestMaxFileSizeBytes = 100L * 1000 * 1000 * 1000;

    /// <summary>
    /// No cap for signed-in accounts: registered, pro and premium users all upload unlimited-size
    /// files, which the host's own <c>?op=api_get_limits</c> corroborates
    /// (<c>&lt;MaxUploadFilesize&gt;0&lt;/MaxUploadFilesize&gt;</c> — the XFileSharing convention for
    /// "unlimited"). The guest cap is applied per-credentials by <see cref="MaxFileSizeFor"/>.
    /// </summary>
    public override long? MaxFileSize => null;

    /// <summary>Per-file cap by tier: guests 100 GB, any signed-in account unlimited.</summary>
    public override long? MaxFileSizeFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? GuestMaxFileSizeBytes : null;

    /// <summary>
    /// Resolves the upload node from Send.now's <b>keyless JSON API</b>, and deliberately never falls
    /// back to scraping the homepage.
    /// <para>
    /// Why the API: <c>GET /api/upload/server</c> answers
    /// <c>{"result":"https://uNNNN.send.now/cgi-bin/upload.cgi?u=api","msg":"OK","status":200}</c> with
    /// no credentials at all, and hands out the SAME rotating node pool the browser's form does
    /// (verified against the 2026-07-26 capture, whose browser posted to <c>u0626</c>, and by sampling
    /// both sources).
    /// </para>
    /// <para>
    /// Why no homepage fallback: <b>fetching the homepage is what trips Cloudflare</b>. A real run got
    /// <c>403</c> + <c>Cf-Mitigated: challenge</c> + <c>cType:'managed'</c> on <c>GET /?_=…</c> while the
    /// API calls from the same client went through untouched. A managed challenge validates the browser
    /// itself, so a fallback there could never succeed — it would only turn a clear API error into a
    /// confusing one and put more challenge-flagged traffic on the user's address.
    /// </para>
    /// <para>
    /// The API labels its URL <c>?u=api</c>; we keep only the node and rebuild the query the browser
    /// actually posts (<c>?upload_type=file&amp;utype=anon</c>, per the capture) so the request stays
    /// byte-shaped like the one that is known to work.
    /// </para>
    /// </summary>
    protected override async Task<(string? UploadUrl, string? Error)> DiscoverAnonymousServerAsync(AttemptContext ctx, CancellationToken ct)
    {
        string json;
        try
        {
            json = await GetAsync(ctx, ApiUploadServerUrl, headers: null, ct);
        }
        catch (Exception ex)
        {
            return (null, $"{Name}: upload-server API request failed: {ex.Message}");
        }

        if (TryReadApiUploadNode(json) is { } node)
        {
            return (node + "?upload_type=file&utype=anon", null);
        }

        // The API is the one endpoint observed to stay clear of the challenge, but say so properly if
        // that ever changes rather than reporting an unhelpful parse failure.
        if (LooksLikeCloudflareChallenge(json))
        {
            return (null,
                $"{Name}: Cloudflare is serving this client its \"Just a moment…\" challenge instead of the "
                + "upload-server API. A managed challenge validates the browser itself (TLS fingerprint, JS "
                + "execution), so no header or cookie sent from here can satisfy it.");
        }

        return (null, $"{Name}: upload-server API returned no usable node: {Snippet(json)}");
    }

    /// <summary>Pulls <c>result</c> out of the upload-server envelope and strips its query, yielding
    /// the bare <c>https://NODE/cgi-bin/upload.cgi</c>. Null when the body isn't that shape (an error
    /// envelope, an HTML challenge page, anything unparseable) so the caller can fall back.
    /// Internal for direct unit testing.</summary>
    internal static string? TryReadApiUploadNode(string json)
    {
        string? result;
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            result = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                     && doc.RootElement.TryGetProperty("result", out System.Text.Json.JsonElement r)
                     && r.ValueKind == System.Text.Json.JsonValueKind.String
                ? r.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(result) || !result.Contains("upload.cgi", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int q = result.IndexOf('?', StringComparison.Ordinal);
        return q < 0 ? result : result[..q];
    }
}
