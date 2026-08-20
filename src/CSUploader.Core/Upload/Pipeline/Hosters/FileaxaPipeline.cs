// <copyright file="FileaxaPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// FILEAXA (fileaxa.com) — <b>anonymous</b> upload on the xfspro chunked plugin. The protocol lives
/// in <see cref="XfsProAnonymousPipeline"/>; this supplies a name and a host. Verified live with
/// real bytes through our own client, 2026-08-02.
/// <para>
/// Its finalise returns a full <c>links.download_link</c>, which the base prefers over rebuilding one
/// from <c>file_code</c>.
/// </para>
/// <para>
/// <b>This host was mis-read twice, both times from its homepage rather than its behaviour.</b> It
/// was first shipped as an account-only shim on <see cref="XFileSharingApiPipeline"/>: the REST API
/// does exist (a bogus key gets the family's <c>{"status":400,"msg":"Invalid key"}</c>) but the site
/// never uses it, so that upload path was never verified — and "no <c>utype=anon</c> form on the
/// homepage" meant only that the uploader is JS-driven, not that anonymous upload was unavailable. A
/// capture of an anonymous AND a signed-in upload settled both: they differ in nothing but
/// <c>sess_id</c>.
/// </para>
/// </summary>
public sealed class FileaxaPipeline : XfsProAnonymousPipeline
{
    public FileaxaPipeline()
    {
    }

    /// <summary>Test ctor — see the base.</summary>
    internal FileaxaPipeline(
        Func<string, Task<HttpResponseSnapshot>> getOverride,
        Func<string, long, long, Task<HttpResponseSnapshot>> chunkOverride,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> finaliseOverride)
        : base(getOverride, chunkOverride, finaliseOverride)
    {
    }

    public override string Name => "FILEAXA";

    /// <summary>Downloads are captcha-free: its premium page checks "No downloads captcha" for
    /// every tier including anonymous, and the live free flow 302s to the bytes
    /// (2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    /// <summary>From its own premium.html plan table (read 2026-08-12), "File retention": guest
    /// "5 days after last download", registered "30 days after last download", premium and pro
    /// "Never".</summary>
    public override FileRetention RetentionFor(Dal.FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? FileRetention.DaysAfterLastDownload(5)
            : credentials.AccountType is AccountType.Premium or AccountType.Pro ? FileRetention.Permanent
            : FileRetention.DaysAfterLastDownload(30);

    protected override string Host => "https://fileaxa.com";
}
