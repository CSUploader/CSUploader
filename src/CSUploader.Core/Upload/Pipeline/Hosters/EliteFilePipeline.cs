// <copyright file="EliteFilePipeline.cs" company="CSUploader">
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
/// EliteFile (elitefile.net) — the most stock XFileSharing host this project has met. From a browser
/// capture of a real signed-in upload 2026-08-03: sign in for the <c>xfss</c> cookie, GET
/// <c>?op=upload_form</c>, scrape the form <c>action</c> + hidden <c>sess_id</c>, post a classic
/// multipart → <c>[{"domain":…,"file_code":…,"file_status":"OK"}]</c>.
/// <para>
/// Unlike every other recent addition it moves nothing: <c>?op=upload_form</c>, <c>?op=my_account</c>,
/// <c>?op=logout</c> and <c>/login.html</c> are all the family defaults, and its form action already
/// carries <c>upload_type=file&amp;utype=reg</c>, so the base's scrape needs no help. Sign-in is the
/// only route — <c>/api/upload/server</c> answers 404, so there is no API to have a key for.
/// </para>
/// <para>
/// <b>⚠ It publishes the link on a DIFFERENT domain than the one you upload to.</b> The upload answers
/// <c>{"domain":"https://elfile.net",…}</c> and the host's own result page links
/// <c>elfile.net/&lt;code&gt;</c>, not <c>elitefile.net/&lt;code&gt;</c>. The base now honours a
/// <c>domain</c> field when the response carries one — added for this host, and correct in general
/// since the server is the authority on its own URL form.
/// </para>
/// <para>
/// <b>⚠ No per-file cap, which has to be said explicitly.</b> Its signed-in uploader config reads
/// <c>max_upload_filesize: '0'</c>, i.e. no limit — and the base's default is <b>1 GiB</b>, so
/// inheriting it would silently skip every larger file at queue time. That is exactly the bug Uploadrar
/// shipped with. Storage is a real published quota instead: 488 GB on the free tier.
/// </para>
/// </summary>
public sealed class EliteFilePipeline : XFileSharingApiPipeline, IStorageRefreshablePipeline
{
    // ?op=my_account: <span>Used Space</span> <div class="price"><sup>GB</sup>0.00 / of 488 <sup>GB</sup></div>
    // Same widget theme as TeraBytez — unit first, then the value — but this one publishes BOTH figures.
    // Anchored on the label because the identical widget two boxes along is "Traffic available", a daily
    // bandwidth allowance that must never be read as storage.
    private static readonly Regex _storageRegex = new(
        """Used\s*Space\s*</span>\s*<div[^>]*\bprice\b[^>]*>\s*<sup>\s*([KMGT]?B)\s*</sup>\s*([0-9]+(?:[.,][0-9]+)?)\s*/\s*of\s*([0-9]+(?:[.,][0-9]+)?)\s*<sup>\s*([KMGT]?B)\s*</sup>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ?op=my_account renders the account header as
    //   <i class="fad fa-user-tie fa-3x"></i><br /><label ...><b>the_account_name</b></label>
    //   <p ...>Free Account</p>
    // Anchored on the fa-3x header icon SPECIFICALLY, because the same theme uses a plain
    // "fa-user-tie" on a nav item labelled "Settings" — and the family's generic fa-user scrape picks
    // that one up, showing every account as "Signed in as Settings". Third host to set this trap after
    // Uploady ("Profile") and Clicknupload (blank), so it is always worth checking against a real page.
    private static readonly Regex _usernameRegex = new(
        """fa-user-tie\s+fa-3x[^>]*>.*?<label[^>]*>\s*<b>\s*([^<]+?)\s*</b>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public EliteFilePipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — drives the form-page GET and the multipart upload from canned responses.</summary>
    internal EliteFilePipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "EliteFile";

    /// <summary>Free downloads are captcha-gated: its free op=download2 form carries the
    /// classic XFS positioned-digit captcha (live flow, 2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    protected override string Host => "https://elitefile.net";

    /// <summary>Web-form (no-API) hoster — its <c>/api/upload/server</c> is a 404.</summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>Storage lives on the account page, not my_files.</summary>
    protected override string WebFormAccountPageUrl => Host + "/?op=my_account";

    /// <summary>
    /// No per-file limit — <c>max_upload_filesize: '0'</c> on the signed-in uploader. Explicitly null
    /// rather than inherited: the base defaults to 1 GiB, which would skip larger files before they
    /// were ever offered to the host.
    /// </summary>
    public override long? MaxFileSize => null;

    /// <summary>
    /// The capture's field set, verbatim: no <c>file_descr</c>, no <c>file_public</c>, no <c>upload</c>
    /// button — six fields where the family default sends nine. This parser is field-presence
    /// sensitive, so the proven set ships.
    /// </summary>
    protected override Dictionary<string, string> BuildClassicExtraFields(string sessId) => new(StringComparer.Ordinal)
    {
        ["sess_id"] = sessId,
        ["utype"] = "reg",
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
        ["keepalive"] = "1",
    };

    /// <summary>
    /// Reads "Used Space <c>0.00 / of 488</c> GB" — both figures, so Available shows a real number
    /// rather than "Unlimited". The neighbouring "Traffic available" widget is bandwidth and is never
    /// matched.
    /// </summary>
    protected override (long? Used, long? Quota) ParseStorageUsage(string html)
    {
        Match m = _storageRegex.Match(html);
        return m.Success
            ? (ParseSizeToBytes(m.Groups[2].Value, m.Groups[1].Value), ParseSizeToBytes(m.Groups[3].Value, m.Groups[4].Value))
            : (null, null);
    }

    /// <summary>
    /// Reads the account name out of the account-page header — see <see cref="_usernameRegex"/> for why
    /// the family's generic <c>fa-user</c> scrape can't be used here (it returns the word "Settings").
    /// Internal for testing.
    /// </summary>
    protected override string? ParseAccountUsername(string html)
    {
        Match m = _usernameRegex.Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Internal for testing — see <see cref="ParseAccountUsername"/>.</summary>
    internal static string? ParseUsername(string html)
    {
        Match m = _usernameRegex.Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Non-interactive storage refresh for the wizard Summary page: re-reads the account page with the
    /// stored <c>xfss</c> cookie (never a WebView). Returns null when there's no usable session.
    /// </summary>
    public Task<StorageUsage?> RefreshStorageAsync(FileHosterLoginDto credentials, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
        => RefreshStorageViaMyFilesAsync(credentials, handler, proxy, ct);
}
