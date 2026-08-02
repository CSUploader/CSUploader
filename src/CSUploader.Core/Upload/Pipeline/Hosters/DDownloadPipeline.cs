// <copyright file="DDownloadPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.RegularExpressions;
using CSUploader.Dal;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// DDownload (ddownload.com, ex ddl.to) — XFileSharing Pro on the base's web-form (no-API) path:
/// WebView sign-in for the <c>xfss</c> cookie, GET the logged-in <c>/upload</c> page, scrape the form
/// <c>action</c> + hidden <c>sess_id</c>, post a classic multipart →
/// <c>[{"file_code":"…","file_status":"OK"}]</c>, link <c>ddownload.com/&lt;code&gt;</c>. Built from a
/// browser capture of a real signed-in upload 2026-08-01.
/// <para>
/// <b>It has a REST API and this deliberately doesn't use it.</b> The API works — it was verified
/// end-to-end, upload and all — but its key is only obtainable from the <i>Affiliate Dashboard</i>
/// (Affiliate → Settings), and the modernised account page no longer renders the family's
/// <c>api-url</c> input or a generate link, so it cannot be bootstrapped either. Requiring every user
/// to enable an affiliate account before their first upload is not a credential flow worth shipping,
/// so signing in is the path and the API is left alone.
/// </para>
/// <para>
/// Three deviations from the family, all found in that capture rather than guessed:
/// <list type="bullet">
///   <item>The uploader is at <b><c>/upload</c></b> (the capture's <c>Referer</c> proves it), not
///   <c>?op=upload_form</c>.</item>
///   <item>Its dashboard links plain <b><c>/logout</c></b>, not <c>?op=logout</c>, so the family's
///   signed-in probe would reject a good session — see <see cref="LooksSignedIn"/>.</item>
///   <item>Storage and identity live in the redesigned dashboard's cards and form rows rather than the
///   stock storage bar — see <see cref="ParseStorageUsage"/> / <see cref="ParseAccountUsername"/>.</item>
/// </list>
/// The multipart field set is the browser's own (eight fields, an EMPTY <c>file_public</c>, no
/// <c>file_descr</c> and no <c>upload</c> button) rather than the family's nine. Both are accepted —
/// the API-path upload used the family default and succeeded — but the proven-in-a-browser set is the
/// one that ships.
/// </para>
/// <para>
/// Free accounts CAN upload here, verified with a real file. Worth stating because it has stopped
/// being the norm: DropGalaxy, Uploady and Clicknupload all advertised free or guest uploads and
/// refused them in practice.
/// </para>
/// <para>
/// <b>No per-file cap is declared.</b> The host publishes no figure (no <c>?op=api_get_limits</c> —
/// this is XFS Pro — and <c>/api/upload/limits</c> answers "Invalid operation"), and its dashboard
/// reports a 5 TB storage allowance. Rather than encode a guess that would silently reject good files,
/// <see cref="MaxFileSize"/> stays null and the server's own refusal is the authority.
/// </para>
/// </summary>
public sealed class DDownloadPipeline : XFileSharingApiPipeline, IStorageRefreshablePipeline
{
    // Dashboard stat card:  <div class="ma-stat-label">… Storage Used</div>
    //                       <div class="ma-stat-value">68 KB</div>
    //                       <div class="ma-stat-sub">of 5.00 TB</div>
    private static readonly Regex _storageUsedRegex = new(
        """Storage\s+Used\s*</div>\s*<div[^>]*\bclass=["']ma-stat-value["'][^>]*>\s*([0-9]+(?:[.,][0-9]+)?)\s*([KMGT]?B)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _storageQuotaRegex = new(
        """class=["']ma-stat-sub["'][^>]*>\s*of\s+([0-9]+(?:[.,][0-9]+)?)\s*([KMGT]?B)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Account Information card:  <div class="ma-form-label">Username</div>
    //                            <div class="ma-form-field"><input type="text" readonly value="NAME">
    private static readonly Regex _usernameRegex = new(
        """>\s*Username\s*</div>\s*<div[^>]*>\s*<input[^>]*\bvalue=["']([^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public DDownloadPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — drives the form-page GET and the multipart upload from canned
    /// responses.</summary>
    internal DDownloadPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "DDownload";

    protected override string Host => "https://ddownload.com";

    /// <summary>Web-form (no-API) hoster — see the class remarks for why the REST API isn't used.</summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>The uploader lives at <c>/upload</c>; this fork has no <c>?op=upload_form</c>.</summary>
    protected override string UploadFormUrl => Host + "/upload";

    /// <summary>The redesigned dashboard is where storage and identity are published.</summary>
    protected override string WebFormAccountPageUrl => MyAccountUrl;

    /// <summary>
    /// No cap enforced client-side: the host declares none, and a wrong guess is the expensive kind of
    /// wrong — it would reject files the server would have taken. See the class remarks.
    /// </summary>
    public override long? MaxFileSize => null;

    /// <summary>
    /// The exact field set DDownload's own uploader posts (browser capture 2026-08-01): eight fields,
    /// an EMPTY <c>file_public</c>, and neither <c>file_descr</c> nor the <c>upload</c> button the
    /// family default sends. XFileSharing's multipart parser is field-presence sensitive, so the
    /// proven set is replicated verbatim.
    /// </summary>
    protected override Dictionary<string, string> BuildClassicExtraFields(string sessId) => new(StringComparer.Ordinal)
    {
        ["sess_id"] = sessId,
        ["utype"] = "reg",
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
        ["file_public"] = string.Empty,
        ["keepalive"] = "1",
    };

    /// <summary>
    /// DDownload's dashboard links plain <c>/logout</c>; the family probe looks for <c>?op=logout</c>
    /// and would therefore reject a perfectly good session. Accept either.
    /// </summary>
    protected override bool LooksSignedIn(string html)
        => html.Contains("op=logout", StringComparison.OrdinalIgnoreCase)
           || html.Contains("/logout", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the dashboard's "Storage Used" card ("68 KB" of "5.00 TB").</summary>
    protected override (long? Used, long? Quota) ParseStorageUsage(string html)
    {
        Match used = _storageUsedRegex.Match(html);
        Match quota = _storageQuotaRegex.Match(html);
        return (used.Success ? ParseSizeToBytes(used.Groups[1].Value, used.Groups[2].Value) : null,
                quota.Success ? ParseSizeToBytes(quota.Groups[1].Value, quota.Groups[2].Value) : null);
    }

    /// <summary>
    /// Reads the account name from the dashboard's "Username" form row. The family's <c>fa-user</c>
    /// scrape can't be used: this theme puts that icon on sidebar tabs, which is exactly how Uploady
    /// ended up saving every account as "Profile".
    /// </summary>
    protected override string? ParseAccountUsername(string html)
    {
        Match m = _usernameRegex.Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Non-interactive storage refresh for the wizard Summary page: re-reads the dashboard with the
    /// stored <c>xfss</c> cookie (never a WebView).
    /// </summary>
    public Task<StorageUsage?> RefreshStorageAsync(FileHosterLoginDto credentials, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
        => RefreshStorageViaMyFilesAsync(credentials, handler, proxy, ct);
}
