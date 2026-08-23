// <copyright file="UploadHivePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.RegularExpressions;
using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// UploadHive (uploadhive.com) — classic XFileSharing with a LIVE anonymous upload, verified
/// 2026-08-08 with real bytes: the family's anonymous field set answers
/// <c>[{"file_code":"…","file_status":"OK"}]</c> and <c>uploadhive.com/&lt;code&gt;</c> serves a
/// download page naming the file.
/// <para>
/// <b>⚠ It nearly slipped through a sweep that should have found it.</b> An earlier pass asked every
/// candidate for <c>?op=api_get_limits</c> — UploadHive answers that with its homepage, so it looked
/// like "not XFileSharing". It is: the family's form is on <c>/upload</c>, not the landing page.
/// <b>A host that doesn't expose one XFS route is not thereby a different platform.</b>
/// </para>
/// <para>
/// <b>The node comes from <c>GET /server</c></b> — <c>{"url":"https://fs430.uploadhive.com/cgi-bin"}</c>
/// — which is what the site's own uploader calls, in both captures. That replaced an earlier scrape
/// of the page: the anonymous FILE form carries no <c>action</c> at all, so the base's regex found
/// only the <i>remote-URL</i> form's (<c>upload_type=url</c>) and files would have gone to the
/// URL-import endpoint. Asking the host beats parsing its HTML, and this host answers.
/// </para>
/// <para>
/// <b>An account changes exactly two fields.</b> Captures of an anonymous and a registered upload
/// (2026-08-08) are otherwise byte-identical — same node, same field set:
/// <code>
/// anonymous   sess_id=""            utype=anon
/// registered  sess_id=&lt;xfss&gt;  utype=reg
/// </code>
/// and <b>that <c>sess_id</c> IS the <c>xfss</c> cookie</b>, verified by comparing the two values in
/// the capture. So the signed-in path needs no <c>?op=upload_form</c> scrape: the session cookie is
/// the session id.
/// </para>
/// <para>
/// Sign-in is a plain <c>op=login</c> form with <b>no captcha</b> (checked in the capture and live),
/// so an account is entered in the app's own dialog and no browser opens.
/// </para>
/// <para>
/// <b>No per-file cap</b>: its uploader config declares <c>max_upload_filesize: '0'</c>, which on this
/// fork means unlimited (a 0 that meant "ten bytes" is what DropGalaxy had — the difference is that
/// uploads here actually succeed). The base's 1 GiB default would silently skip every larger file, so
/// it is overridden rather than inherited.
/// </para>
/// </summary>
public sealed class UploadHivePipeline : XFileSharingApiPipeline
{
    /// <summary>
    /// Extensions the host refuses, taken from its own uploader config
    /// (<c>ext_not_allowed: '7z|001'</c>) and confirmed by uploading one of each: both come back
    /// <c>{"file_code":"undef","file_status":"unallowed extension"}</c> — <b>after</b> the whole file
    /// has transferred, which is exactly what this hook exists to prevent.
    /// <para>
    /// ⚠ <c>.001</c> is the first volume of a split archive, so a package can be refused at its most
    /// important part while every other volume uploads happily.
    /// </para>
    /// </summary>
    private static readonly string[] BlockedExtensions = [".7z", ".001"];

    public UploadHivePipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow.</summary>
    internal UploadHivePipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "UploadHive";

    /// <summary>Free downloads are captcha-gated: its premium page sells "No downloads captcha"
    /// as a premium-only perk (uploadhive.com/premium, 2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    /// <summary>From its own premium.html (read 2026-08-12): free 50, registered 140, "days after
    /// last download"; premium "Never".</summary>
    public override FileRetention RetentionFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? FileRetention.DaysAfterLastDownload(50)
            : credentials.AccountType == AccountType.Premium ? FileRetention.Permanent
            : FileRetention.DaysAfterLastDownload(140);

    protected override string Host => "https://uploadhive.com";

    /// <summary>Verified by uploading real bytes and fetching the resulting page — not by the form
    /// rendering, which DropGalaxy, Uploady and Clicknupload all did while refusing the bytes.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>See the class remarks: the host declares no ceiling, and the base's 1 GiB default
    /// would reject larger files before they were ever offered.</summary>
    public override long? MaxFileSize => null;

    /// <inheritdoc/>
    public override string? RejectedFileExtensionReason(string fileName)
        => BlockedExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase)
            ? $"{Name} doesn't accept {Path.GetExtension(fileName).ToLowerInvariant()} files."
            : null;

    // Its account page is /account/, and NONE of the family's markers are on it: no ?op=logout, no
    // fa-user icon, no class="storage". These three come from the page's own markup (capture
    // 2026-08-08) rather than from its tag-stripped text — a pattern written against stripped text is
    // what made every upload.ee sign-in "fail" while its tests passed.

    /// <summary><c>&lt;div class="UserHead"&gt;… Welcome back &lt;b&gt;name&lt;/b&gt;, this is your userpanel</c></summary>
    private static readonly Regex _welcomeRegex = new(
        """Welcome\s+back\s*(?:<[^>]*>\s*)*([^<>,\r\n]+?)\s*(?:</[^>]*>\s*)*,""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary><c>&lt;div class="txt1"&gt;Used space&lt;/div&gt; &lt;div class="txt2"&gt;0.00 of 98 GB&lt;/div&gt;</c>
    /// — no unit on the used figure, so it takes the total's.</summary>
    private static readonly Regex _usedSpaceRegex = new(
        """Used\s+space\s*</div>\s*<div[^>]*>\s*([0-9]+(?:[.,][0-9]+)?)\s*(?:([KMGT]?B)\s*)?of\s*([0-9]+(?:[.,][0-9]+)?)\s*([KMGT]?B)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Test seams for the three account-page scrapes. All three are host-specific overrides
    /// of a family default that silently returns nothing here, which is the failure worth pinning.</summary>
    internal bool LooksSignedInForTests(string html) => LooksSignedIn(html);

    /// <inheritdoc cref="LooksSignedInForTests"/>
    internal string? ParseAccountUsernameForTests(string html) => ParseAccountUsername(html);

    /// <inheritdoc cref="LooksSignedInForTests"/>
    internal (long? Used, long? Quota) ParseStorageUsageForTests(string html) => ParseStorageUsage(html);

    /// <summary>The account page, which is not the family's <c>?op=my_files</c>.</summary>
    protected override string WebFormAccountPageUrl => Host + "/account/";

    /// <summary>Its logout is a plain <c>/logout/</c> link, not the family's <c>?op=logout</c>, so the
    /// default detector reads a perfectly good sign-in as failed.</summary>
    protected override bool LooksSignedIn(string html)
        => html.Contains("/logout", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    protected override string? ParseAccountUsername(string html)
    {
        Match m = _welcomeRegex.Match(html);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <inheritdoc/>
    protected override (long? Used, long? Quota) ParseStorageUsage(string html)
    {
        Match m = _usedSpaceRegex.Match(html);
        if (!m.Success)
        {
            return (null, null);
        }

        // "0.00 of 98 GB" — the used figure carries no unit of its own, so it borrows the total's.
        string usedUnit = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[4].Value;
        return (ParseSizeToBytes(m.Groups[1].Value, usedUnit), ParseSizeToBytes(m.Groups[3].Value, m.Groups[4].Value));
    }

    /// <summary><c>GET /server</c> answers <c>{"url":"https://fsNNN.uploadhive.com/cgi-bin"}</c>.</summary>
    private static readonly Regex _serverRegex = new(
        """"url"\s*:\s*"([^"]+)"""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Asks the host which node to use, as its own uploader does.</summary>
    private async Task<(string? UploadUrl, string? Error)> ResolveNodeAsync(AttemptContext ctx, string query)
    {
        string json;
        try
        {
            json = await GetAsync(ctx, $"{Host}/server", NoCacheHeaders, ctx.Cancellation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, $"{Name}: upload-node lookup failed: {ex.Message}");
        }

        Match m = _serverRegex.Match(json);
        if (!m.Success)
        {
            return (null, LooksLikeCloudflareChallenge(json)
                ? $"{Name}: Cloudflare is serving this client its \"Just a moment…\" challenge instead of the "
                  + "upload-node lookup. A managed challenge validates the browser itself, so no header or "
                  + "cookie sent from here can satisfy it."
                : $"{Name}: /server returned no node: {Snippet(json)}");
        }

        return ($"{m.Groups[1].Value.TrimEnd('/')}/upload.cgi?{query}", null);
    }

    /// <inheritdoc/>
    protected override Task<(string? UploadUrl, string? Error)> DiscoverAnonymousServerAsync(AttemptContext ctx, CancellationToken ct)
    {
        _ = ct;
        return ResolveNodeAsync(ctx, "upload_type=file&utype=anon");
    }

    /// <summary>
    /// The signed-in node, and the session id to send with it. Both captures post to the same
    /// <c>GET /server</c> node, and the <c>sess_id</c> field is simply the <c>xfss</c> cookie — so
    /// unlike the rest of this family there is no <c>?op=upload_form</c> page to scrape.
    /// </summary>
    protected override async Task<(string? UploadUrl, string? SessId, string? Error, bool AuthExpired)> ResolveWebFormUploadServerAsync(
        AttemptContext ctx, string html, string xfss, CancellationToken ct)
    {
        _ = html;
        _ = ct;

        (string? url, string? error) = await ResolveNodeAsync(ctx, "upload_type=file&utype=reg").ConfigureAwait(false);
        return url is null ? (null, null, error, false) : (url, xfss, null, false);
    }

    /// <summary>
    /// The field set both captures send, which differs from the family default: it carries
    /// <c>file_descr</c> rather than <c>file_0_descr</c>, sets <c>file_public=1</c>, and sends no
    /// <c>mode</c>, <c>keepalive</c> or <c>submit_btn</c>. The host accepts the family's set too — an
    /// early probe with it succeeded — but this is what its own uploader sends.
    /// </summary>
    protected override Dictionary<string, string> BuildAnonymousExtraFields() => new(StringComparer.Ordinal)
    {
        ["sess_id"] = string.Empty,
        ["utype"] = "anon",
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
        ["file_descr"] = string.Empty,
        ["file_public"] = "1",
    };

    /// <inheritdoc cref="BuildAnonymousExtraFields"/>
    protected override Dictionary<string, string> BuildClassicExtraFields(string sessId) => new(StringComparer.Ordinal)
    {
        ["sess_id"] = sessId,
        ["utype"] = "reg",
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
        ["file_descr"] = string.Empty,
        ["file_public"] = "1",
    };

    /// <summary>No API to have a key for, so an account signs in and uploads through the node with its
    /// <c>xfss</c> as the <c>sess_id</c>.</summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>Its login is a plain form with no captcha — checked in the capture and on the live
    /// page — so credentials go in the app's own dialog and no sign-in window opens.</summary>
    protected override bool SupportsDirectLogin => true;

}
