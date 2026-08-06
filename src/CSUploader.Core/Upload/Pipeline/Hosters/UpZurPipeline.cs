// <copyright file="UpZurPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.RegularExpressions;
using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// UpZur (upzur.com) — classic XFileSharing with a LIVE anonymous upload, verified 2026-08-06 by
/// uploading real bytes: the family's anonymous field set posted to the node answers
/// <c>[{"file_code":"…","file_status":"OK"}]</c>, and <c>upzur.com/&lt;code&gt;</c> serves a download
/// page naming the file. It was offered on a candidate list marked <i>"Sign-Up Required"</i>; it is not.
/// <para>
/// <b>Its homepage renders no upload form</b>, so the base's usual scrape (a
/// <c>&lt;form action="…/upload.cgi…"&gt;</c> on the landing page) finds nothing here. The node comes
/// from the host's own API instead — <c>?op=api_get_limits</c>, which every stock XFS exposes
/// keylessly:
/// <code>
/// &lt;Data&gt;&lt;ExtAllowed&gt;&lt;/ExtAllowed&gt;&lt;ExtNotAllowed&gt;&lt;/ExtNotAllowed&gt;
///   &lt;MaxUploadFilesize&gt;200&lt;/MaxUploadFilesize&gt;
///   &lt;ServerURL&gt;https://systeme.upzur.com/cgi-bin&lt;/ServerURL&gt;
///   &lt;SessionID&gt;&lt;/SessionID&gt;&lt;SiteName&gt;UpZur&lt;/SiteName&gt;&lt;/Data&gt;
/// </code>
/// That is the sturdier source anyway: an HTML landing page is subject to WAF and marketing
/// variation, where this contract is the one the host's own clients use. Same reasoning as
/// <see cref="SendNowPipeline"/> preferring <c>/api/upload/server</c> — but note UpZur has no such
/// route (it 404s), which is why the limits call carries the node here.
/// </para>
/// <para>
/// <b>200 MB anonymous</b>, per <c>MaxUploadFilesize</c> above — read from the keyless call, so it is
/// the guest figure. The candidate list advertised "5GB / 1.95TB"; those are the paid tiers, and the
/// host's own API is the authority over a third-party list. <c>ExtNotAllowed</c> is empty, so unlike
/// Uploadrar and filedot there is nothing to reject up front.
/// </para>
/// </summary>
public sealed class UpZurPipeline : XFileSharingApiPipeline
{
    /// <summary>The keyless limits call, which is also where the upload node comes from.</summary>
    private const string ApiGetLimitsPath = "/?op=api_get_limits";

    /// <summary>&lt;ServerURL&gt;https://systeme.upzur.com/cgi-bin&lt;/ServerURL&gt; — the cgi-bin
    /// DIRECTORY, not the script, so the script name is appended below.</summary>
    private static readonly Regex _serverUrlRegex = new(
        """<ServerURL>\s*([^<\s]+)\s*</ServerURL>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public UpZurPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow from
    /// canned responses.</summary>
    internal UpZurPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "UpZur";

    protected override string Host => "https://upzur.com";

    /// <summary>Verified by uploading a file and fetching the resulting page — not by a form being
    /// rendered, which is what DropGalaxy, Uploady and Clicknupload each had while refusing the
    /// bytes.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>The host's own <c>MaxUploadFilesize</c> (MB) from the keyless limits call. Binary,
    /// as XFileSharing's limits are 1024-based.</summary>
    private const long AnonymousMaxFileSizeBytes = 200L * 1024 * 1024;

    /// <summary>Guest cap. The account path keeps the family default — no account has been used here,
    /// so nothing stronger is claimed for it.</summary>
    public override long? MaxFileSizeFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? AnonymousMaxFileSizeBytes : base.MaxFileSizeFor(credentials);

    /// <summary>
    /// Reads the node out of <c>?op=api_get_limits</c> rather than off a form, because this host
    /// renders no anonymous form to scrape. The query appended to the script is the family's own
    /// (<c>upload_type=file&amp;utype=anon</c>) — the same request that was verified live.
    /// </summary>
    protected override async Task<(string? UploadUrl, string? Error)> DiscoverAnonymousServerAsync(AttemptContext ctx, CancellationToken ct)
    {
        string xml;
        try
        {
            xml = await GetAsync(ctx, Host + ApiGetLimitsPath, NoCacheHeaders, ct);
        }
        catch (Exception ex)
        {
            return (null, $"{Name}: upload-server lookup failed: {ex.Message}");
        }

        Match m = _serverUrlRegex.Match(xml);
        if (!m.Success)
        {
            if (LooksLikeCloudflareChallenge(xml))
            {
                return (null,
                    $"{Name}: Cloudflare is serving this client its \"Just a moment…\" challenge instead of "
                    + "the limits call. A managed challenge validates the browser itself (TLS fingerprint, "
                    + "JS execution), so no header or cookie sent from here can satisfy it.");
            }

            return (null, $"{Name}: ?op=api_get_limits carried no <ServerURL>: {Snippet(xml)}");
        }

        string node = m.Groups[1].Value.TrimEnd('/');
        return ($"{node}/upload.cgi?upload_type=file&utype=anon", null);
    }
}
