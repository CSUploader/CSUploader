// <copyright file="HxfilePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Hxfile. Standard XFileSharingPro API — verified end-to-end during the 2026-05-29
/// probe sweep (both REST endpoints responding with the canonical
/// <c>{status, msg, server_time}</c> shape; <c>/login.html</c> served direct, no
/// redirects). Only Name + Host required; protocol lives in
/// <see cref="XFileSharingApiPipeline"/>.
/// </summary>
public sealed class HxfilePipeline : XFileSharingApiPipeline
{
    public HxfilePipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    internal HxfilePipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Hxfile";

    /// <summary>Free downloads are captcha-gated: its free op=download2 form embeds a visible
    /// reCAPTCHA v2 widget (live page, 2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    /// <summary>Its premium plans each say "Your files stored 365 days" (read 2026-08-12) - premium
    /// only, basis unstated, so this is the from-upload floor. No figure is published for free
    /// accounts.</summary>
    public override FileRetention RetentionFor(FileHosterLoginDto credentials)
        => !credentials.IsAnonymous && credentials.AccountType == AccountType.Premium
            ? FileRetention.DaysAfterUpload(365)
            : FileRetention.Unspecified;

    protected override string Host => "https://hxfile.co";

    /// <summary>
    /// Hxfile's CDN frontend (<c>ctmp.world</c> per the 2026-06-01 Fiddler trace) uses
    /// the modern chunked upload protocol — per-chunk POSTs to <c>up.cgi</c> followed
    /// by a finalize call to <c>api.cgi</c>. The classic <c>upload.cgi</c> endpoint
    /// returns 404 on this hoster.
    /// </summary>
    protected override bool UsesChunkedUpload => true;
}
