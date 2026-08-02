// <copyright file="SendNowPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Send.now — classic XFileSharing; the upload protocol lives in <see cref="XFileSharingApiPipeline"/>.
/// <para>
/// Formerly <b>send.cm</b> (and tusfiles / sendit before that): send.cm now 301s to send.now, so the
/// live brand is the one wired here — a single entry covers traffic addressed to either. The upload
/// itself is the family's ordinary anonymous POST (empty <c>sess_id</c>, <c>utype=anon</c>,
/// <c>file_0</c>, answered with <c>[{"file_status":"OK","file_code":…}]</c>), confirmed against a live
/// browser capture 2026-07-26.
/// </para>
/// <para>
/// <b>Getting an upload node here is the whole difficulty</b>, and the constraints are contradictory:
/// <list type="bullet">
///   <item><b>Every page-style path is unusable.</b> <c>send.now</c> sits behind a Cloudflare
///   <i>managed</i> challenge: both <c>GET /?_=…</c> (the homepage form) and <c>GET /?op=api_get_limits</c>
///   answered a real client with <c>403</c> + <c>Cf-Mitigated: challenge</c> + <c>cType:'managed'</c>.
///   A managed challenge validates the browser itself, so no amount of header shaping gets past it.</item>
///   <item><b><c>/api/*</c> is the ONLY route Cloudflare lets through</b> — which is why it returns real
///   answers (even refusals) rather than an interstitial. So the API is not a preference here, it is the
///   only option.</item>
///   <item><b>…but the API punishes volume.</b> <c>/api/upload/server</c> is an API-<i>key</i> endpoint;
///   it serves keyless callers for a while, then counts the calls as failed authentications and returns
///   HTTP 200 carrying <c>{"status":429,"msg":"Too many failed attempts. Please try again in 60
///   minutes."}</c> — an hour-long IP lockout. A package that looked one node up PER FILE tripped it in
///   a single run.</item>
/// </list>
/// The reconciliation is to call the one permitted endpoint as seldom as possible: the node is
/// <b>cached for the batch</b> (see <see cref="DiscoverAnonymousServerAsync"/>) so a package costs one
/// lookup instead of one per file, and <see cref="MaxConcurrentUploadsFor"/> caps parallelism.
/// </para>
/// <para>
/// Known risk for the ACCOUNT path (untested — no account has been used yet): the base's
/// <c>CheckAccountAsync</c> scrapes <c>?op=my_account</c> with the C# handler to extract the API key, and
/// that is an HTML page on the challenged domain. Signed-in uploads are fine (a real API key makes
/// <c>/api/upload/server</c> the endpoint it was designed to be), so if sign-in fails with a challenge
/// the fix is to source the key without touching HTML — not to abandon the hoster.
/// </para>
/// </summary>
public sealed class SendNowPipeline : XFileSharingApiPipeline
{
    /// <summary>How long a resolved node is reused before being looked up again. Nodes rotate, so this
    /// stays short; the point is only to collapse a package's worth of lookups into one, because each
    /// lookup counts against the host's failed-attempt lockout.</summary>
    private static readonly TimeSpan NodeCacheLifetime = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _nodeGate = new(1, 1);
    private readonly HashSet<Guid> _servedAttempts = [];
    private string? _cachedNode;
    private DateTime _cachedNodeExpiresUtc;

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

    /// <summary>Anonymous (not-logged-in) upload verified against the live site.</summary>
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
    /// Cap simultaneous uploads at four. Send.now polices request volume aggressively (its keyless API
    /// hands out hour-long lockouts), and four parallel uploads is what was observed to work; the
    /// scheduler takes the min of this and the user's own per-host setting, so this only ever narrows.
    /// </summary>
    public override int? MaxConcurrentUploadsFor(FileHosterLoginDto credentials) => 4;

    /// <summary>
    /// Resolves the upload node from <c>/api/upload/server</c> — the only path Cloudflare lets through —
    /// and <b>caches it for the batch</b>.
    /// <para>
    /// The caching is not an optimisation, it is what makes the endpoint usable at all: keyless calls
    /// count against a failed-attempt lockout, and a package that looked one node up per file earned a
    /// 60-minute ban in a single run. One lookup now serves the whole batch — the same reason
    /// <c>GofilePipeline</c> caches its guest account against gofile's per-IP limit.
    /// </para>
    /// <para>
    /// A retry after an unreachable node re-enters this method with the SAME attempt id; that is taken
    /// as "the node I just gave you is dead", so the cache is dropped and a fresh node fetched — the
    /// rotating-node retry keeps working.
    /// </para>
    /// </summary>
    protected override async Task<(string? UploadUrl, string? Error)> DiscoverAnonymousServerAsync(AttemptContext ctx, CancellationToken ct)
    {
        await _nodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Add returns false when this attempt has already been handed the cached node — i.e. it came
            // back because that node failed.
            bool attemptRepeating = !_servedAttempts.Add(ctx.AttemptId);
            bool usable = _cachedNode is not null
                          && DateTime.UtcNow < _cachedNodeExpiresUtc
                          && !attemptRepeating;

            if (usable)
            {
                return (BuildUploadUrl(_cachedNode!), null);
            }

            string json;
            try
            {
                json = await GetAsync(ctx, ApiUploadServerUrl, headers: null, ct);
            }
            catch (Exception ex)
            {
                return (null, $"{Name}: upload-server lookup failed: {ex.Message}");
            }

            if (TryReadApiUploadNode(json) is not { } node)
            {
                return (null, DescribeUnusableLookup(json));
            }

            _cachedNode = node;
            _cachedNodeExpiresUtc = DateTime.UtcNow.Add(NodeCacheLifetime);
            _servedAttempts.Clear();
            _servedAttempts.Add(ctx.AttemptId);
            return (BuildUploadUrl(node), null);
        }
        finally
        {
            _nodeGate.Release();
        }
    }

    /// <summary>Explains a lookup that produced no node, preferring the host's own words. The
    /// interesting case is the lockout — <c>{"status":429,"msg":"Too many failed attempts. Please try
    /// again in 60 minutes."}</c> arriving with HTTP 200 — which is worth quoting verbatim, since it
    /// tells the user both what happened and how long to wait.</summary>
    private string DescribeUnusableLookup(string body)
    {
        if (LooksLikeCloudflareChallenge(body))
        {
            return $"{Name}: Cloudflare is serving this client its \"Just a moment…\" challenge instead of the "
                   + "upload-server lookup. A managed challenge validates the browser itself (TLS fingerprint, "
                   + "JS execution), so no header or cookie sent from here can satisfy it.";
        }

        return TryReadApiMessage(body) is { } msg
            ? $"{Name}: upload-server lookup refused: {msg}"
            : $"{Name}: upload-server lookup returned no usable node: {Snippet(body)}";
    }

    /// <summary>Builds the POST target from the node URL the API hands back. That URL already ends in
    /// the script (<c>…/cgi-bin/upload.cgi</c> — <see cref="TryReadApiUploadNode"/> requires it and only
    /// strips the query), so all that is added is the query the browser itself sends (captured
    /// 2026-07-26), keeping the request byte-shaped like the known-good one.</summary>
    private static string BuildUploadUrl(string nodeScriptUrl)
        => nodeScriptUrl + "?upload_type=file&utype=anon";

    /// <summary>Pulls <c>result</c> out of the upload-server envelope and strips its query, yielding the
    /// bare <c>https://NODE/cgi-bin/upload.cgi</c>. Null when the body isn't that shape (a refusal
    /// envelope, a challenge page, anything unparseable). Internal for testing.</summary>
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

    /// <summary>The <c>msg</c> the API answered with, when it answered in its own envelope rather than
    /// handing out a node. Null for anything that isn't that shape. Internal for testing.</summary>
    internal static string? TryReadApiMessage(string json)
    {
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("msg", out System.Text.Json.JsonElement m)
                   && m.ValueKind == System.Text.Json.JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(m.GetString())
                ? m.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
