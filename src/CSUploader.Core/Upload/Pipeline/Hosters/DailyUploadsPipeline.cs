// <copyright file="DailyUploadsPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// DailyUploads (dailyuploads.net) — <b>anonymous</b> upload on the xfspro chunked plugin. The
/// protocol lives in <see cref="XfsProAnonymousPipeline"/>; this supplies a name and a host.
/// Verified live with real bytes, 2026-08-02: 256 KB uploaded with an empty <c>sess_id</c>, and the
/// resulting page names the file.
/// <para>
/// <b>Its finalise returns only a <c>file_code</c></b> — no <c>links</c> object — so the share link
/// is <c>dailyuploads.net/&lt;code&gt;</c>, which the base builds. That difference from FILEAXA is
/// the reason the base handles both reply shapes.
/// </para>
/// <para>
/// <b>Found by correcting a bad sweep.</b> This host sat in the candidate list marked anonymous, was
/// swept along with nine others, and was written off because its homepage renders no
/// <c>utype=anon</c> / <c>upload.cgi</c> form. That test was wrong — like FILEAXA, its uploader is
/// JS-driven, and asking <c>GET /server</c> instead gets a keyless node straight away.
/// </para>
/// </summary>
public sealed class DailyUploadsPipeline : XfsProAnonymousPipeline
{
    public DailyUploadsPipeline()
    {
    }

    /// <summary>Test ctor — see the base.</summary>
    internal DailyUploadsPipeline(
        Func<string, Task<HttpResponseSnapshot>> getOverride,
        Func<string, long, long, Task<HttpResponseSnapshot>> chunkOverride,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> finaliseOverride)
        : base(getOverride, chunkOverride, finaliseOverride)
    {
    }

    public override string Name => "DailyUploads";

    /// <summary>Downloads are captcha-free: the live free flow (download1, 30s wait, download2)
    /// returns the bytes with no captcha field anywhere (2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    /// <summary>From its own premium.html (read 2026-08-12), "When are your files deleted?": guest
    /// "1 days after last download", registered 15, premium 40. One download a day is all that keeps
    /// a guest file alive.</summary>
    public override FileRetention RetentionFor(Dal.FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? FileRetention.DaysAfterLastDownload(1)
            : credentials.AccountType == AccountType.Premium ? FileRetention.DaysAfterLastDownload(40)
            : FileRetention.DaysAfterLastDownload(15);

    protected override string Host => "https://dailyuploads.net";
}
