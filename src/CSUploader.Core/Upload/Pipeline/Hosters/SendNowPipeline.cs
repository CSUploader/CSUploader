// <copyright file="SendNowPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.RegularExpressions;
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
/// <b>Getting an upload node here is the whole difficulty</b>, because two of the three obvious sources
/// are traps:
/// <list type="bullet">
///   <item><b>The homepage form</b> — behind a Cloudflare <i>managed</i> challenge. A real run got
///   <c>403</c> + <c>Cf-Mitigated: challenge</c> + <c>cType:'managed'</c> just for <c>GET /?_=…</c>.</item>
///   <item><b><c>/api/upload/server</c></b> — an API-<i>key</i> endpoint. Keyless calls answer normally
///   at first, which is exactly how it fooled an earlier version of this pipeline, but the host counts
///   them as failed authentications: after a modest number it returns HTTP 200 carrying
///   <c>{"status":429,"msg":"Too many failed attempts. Please try again in 60 minutes."}</c> and locks
///   the IP out for an hour. A multi-file package tripped it in one run. <b>Do not call it anonymously.</b></item>
///   <item><b><c>?op=api_get_limits</c></b> — the XFileSharing session/limits endpoint, which returns
///   <c>&lt;ServerURL&gt;https://uNNNN.send.now/cgi-bin&lt;/ServerURL&gt;</c>. Still served normally while
///   the API endpoint was locked out, so this is what the pipeline uses.</item>
/// </list>
/// On top of that the node is <b>cached</b> (see <see cref="DiscoverAnonymousServerAsync"/>) so a package
/// costs one lookup rather than one per file, and concurrency is capped — this host punishes volume.
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
    /// <summary>XFileSharing's session/limits endpoint — the one node source that stays available.</summary>
    private string LimitsUrl => Host + "/?op=api_get_limits";

    /// <summary>How long a resolved node is reused before being looked up again. Nodes rotate, so this
    /// stays short; the point is only to collapse a package's worth of lookups into one.</summary>
    private static readonly TimeSpan NodeCacheLifetime = TimeSpan.FromMinutes(10);

    // <ServerURL>https://uNNNN.send.now/cgi-bin</ServerURL> — the cgi-bin directory, WITHOUT the script.
    private static readonly Regex _serverUrlRegex = new(
        """<ServerURL>\s*([^<\s]+)\s*</ServerURL>""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
    /// Resolves the upload node from <c>?op=api_get_limits</c> and <b>caches it for the batch</b>.
    /// <para>
    /// The caching is not an optimisation, it is the fix for a real failure: every queued file used to
    /// perform its own lookup, and this host treats a burst of anonymous lookups as abuse — a package
    /// was enough to earn a 60-minute lockout. One lookup now serves the whole batch, the same reason
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

            string xml;
            try
            {
                xml = await GetAsync(ctx, LimitsUrl, headers: null, ct);
            }
            catch (Exception ex)
            {
                return (null, $"{Name}: upload-server lookup failed: {ex.Message}");
            }

            if (TryReadServerUrl(xml) is not { } node)
            {
                return (null, LooksLikeCloudflareChallenge(xml)
                    ? $"{Name}: Cloudflare is serving this client its \"Just a moment…\" challenge instead of the "
                      + "upload-server lookup. A managed challenge validates the browser itself (TLS fingerprint, "
                      + "JS execution), so no header or cookie sent from here can satisfy it."
                    : $"{Name}: upload-server lookup returned no ServerURL: {Snippet(xml)}");
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

    /// <summary>Builds the POST target from a node directory, using the query the browser itself sends
    /// (captured 2026-07-26) so the request stays byte-shaped like the known-good one.</summary>
    private static string BuildUploadUrl(string nodeDirectory)
        => nodeDirectory.TrimEnd('/') + "/upload.cgi?upload_type=file&utype=anon";

    /// <summary>Pulls <c>ServerURL</c> out of the <c>?op=api_get_limits</c> XML — the node DIRECTORY
    /// (<c>https://uNNNN.send.now/cgi-bin</c>), without the script name. Null when the response isn't
    /// that shape (an error envelope, a challenge page, anything unparseable). Internal for testing.</summary>
    internal static string? TryReadServerUrl(string xml)
    {
        Match m = _serverUrlRegex.Match(xml);
        if (!m.Success)
        {
            return null;
        }

        string url = m.Groups[1].Value.Trim();
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
    }
}
