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
/// Probed live 2026-07-26: the homepage renders the family's anonymous form
/// (<c>&lt;form action="https://dlNNNN.send.now/cgi-bin/upload.cgi?upload_type=file&amp;utype=anon"&gt;</c>
/// with an empty <c>sess_id</c>), so the base's anonymous path applies unchanged. It is genuinely
/// stock XFS: <c>?op=api_get_limits</c> answers with the standard
/// <c>&lt;Data&gt;…&lt;ServerURL&gt;…</c> XML, and <c>/api/upload/server</c> hands out a node. Cloudflare
/// is passive (plain GETs succeed from the C# stack).
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

    /// <summary>
    /// No client-side per-file cap: the host's own <c>?op=api_get_limits</c> reports
    /// <c>&lt;MaxUploadFilesize&gt;0&lt;/MaxUploadFilesize&gt;</c> — the XFileSharing convention for
    /// "unlimited" — while the marketing page advertises multi-GB uploads. Rather than invent a
    /// number that could reject a file the server would have accepted, let the server be the gate;
    /// it rejects an over-limit upload up front (no wasted bytes) the way the other uncapped
    /// anonymous hosters do.
    /// </summary>
    public override long? MaxFileSize => null;

    /// <summary>
    /// Resolves the upload node from Send.now's <b>keyless JSON API</b> rather than by scraping the
    /// homepage form, falling back to the family's HTML scrape if the API is unhelpful.
    /// <para>
    /// Why: <c>GET /api/upload/server</c> answers
    /// <c>{"result":"https://uNNNN.send.now/cgi-bin/upload.cgi?u=api","msg":"OK","status":200}</c> with
    /// no credentials at all, and hands out the SAME rotating node pool the browser's form does
    /// (verified against the user's 2026-07-26 capture, whose browser posted to <c>u0626</c>, and by
    /// sampling both sources). A JSON contract is a far better thing to depend on than the marketing
    /// homepage, which sits behind Cloudflare and demonstrably renders differently for different
    /// clients — a real user hit "anonymous upload form not found" on a page that serves the form
    /// fine to other callers.
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
        catch (Exception)
        {
            // Transport trouble on the API — let the HTML scrape have a go before failing.
            return await base.DiscoverAnonymousServerAsync(ctx, ct);
        }

        if (TryReadApiUploadNode(json) is { } node)
        {
            return (node + "?upload_type=file&utype=anon", null);
        }

        return await base.DiscoverAnonymousServerAsync(ctx, ct);
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
