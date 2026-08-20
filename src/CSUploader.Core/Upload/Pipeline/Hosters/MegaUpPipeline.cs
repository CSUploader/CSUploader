// <copyright file="MegaUpPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// MegaUp (megaup.net) — <see cref="YetiSharePipeline"/> with a <b>guest</b> upload, verified
/// 2026-08-08 by uploading real bytes: a signed-out <c>GET /assets/js/uploader.js</c> hands back a
/// complete ticket and a <b>5 GiB</b> cap, and the node answers
/// <c>[{"error":null,"url":"https://megaup.net/&lt;hash&gt;/&lt;name&gt;","delete_url":…}]</c>.
/// <para>
/// It was found by fingerprint rather than by any list: its uploader script declares
/// <c>uploaderMaxSize = 5368709120</c> and <c>maxChunkSize = 100000000</c>, <b>byte-identical to
/// udrop</b>, which is what identified the platform before a single upload was attempted. The list
/// this app was working from had it filed only as "5 GB (200 GB prem), confirm anon vs account".
/// </para>
/// <para>
/// <b>Its node is a separate storage host</b> (<c>f1NN.mupload.store</c>, rotating) on a different
/// domain from the site, so this is BowFile's half of the pattern rather than udrop's: the site
/// cookie never reaches the node and isn't needed — <b>an upload with no cookie at all succeeds</b>,
/// because the node authenticates on the <c>_sessionid</c> FIELD. Measured both ways. The base picks
/// the behaviour by comparing hosts, so this needs no flag.
/// </para>
/// <para>
/// The share link comes back on the apex (<c>megaup.net/&lt;hash&gt;/&lt;name&gt;</c>) and is used
/// exactly as returned.
/// </para>
/// </summary>
public sealed class MegaUpPipeline : YetiSharePipeline
{
    public MegaUpPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — drives the uploader.js scrape and the upload from canned responses.</summary>
    internal MegaUpPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "MegaUp";

    /// <summary>Downloads are captcha-free: the ordinary free flow is a 2s countdown revealing the
    /// final direct download link, with no captcha widget (2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    /// <summary>From its own FAQ (read 2026-08-12): "All files will be deleted after 30 days
    /// inactivity." - every tier, and inactivity means the countdown restarts on a
    /// download.</summary>
    public override FileRetention RetentionFor(FileHosterLoginDto credentials)
        => FileRetention.DaysAfterLastDownload(30);

    protected override string SiteBase => "https://megaup.net";

    /// <summary>Verified by uploading a file as a signed-out visitor — not by the ticket merely being
    /// rendered, which Filestank also does before refusing the bytes.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>
    /// Its sign-in is the family's plain <c>username</c>/<c>password</c>/<c>submitme</c> form with
    /// <b>no captcha</b>, at the base's own <c>/account/login</c> — so an account is entered in the
    /// app's own dialog and no browser ever opens.
    /// </summary>
    protected override bool SupportsDirectLogin => true;

    /// <summary>
    /// The cap its uploader script declares — <c>uploaderMaxSize = 5368709120</c>, the same 5 GiB
    /// udrop gives.
    /// <para>
    /// Its pricing page advertises <b>200 GB</b> for premium, which is a <i>storage</i> allowance and
    /// not a per-file limit; the per-file figure the uploader hands this session is the one that
    /// governs, and the base re-reads it per upload anyway.
    /// </para>
    /// </summary>
    protected override long UploaderMaxSize => 5_368_709_120;

    // Deliberately no RejectedFileExtensionReason: unlike udrop — whose admin blocks .bin — this host
    // accepted .rar, .r00, .sfv, .nfo AND .bin as a guest, each measured. Copying udrop's blocklist
    // over would refuse files this host would have taken.
}
