// <copyright file="FileBoomPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// FileBoom (fboom.me, marketed as fileboom.me) — one of the two "moneyplatform" sister sites. The
/// entire protocol (WebView accessToken sign-in, <c>/v1/files/upload-url</c> + filestore.app
/// multipart, <c>/v1/users/me/statistic</c>) lives in <see cref="MoneyPlatformPipeline"/>; this
/// subclass supplies only the fboom.me domains and the free-tier per-file cap.
/// </summary>
public sealed class FileBoomPipeline : MoneyPlatformPipeline
{
    /// <summary>FileBoom free-tier per-file ceiling — 1 GiB. (Tier-aware MaxFileSize isn't modelled,
    /// so we surface the conservative free value.)</summary>
    private const long FreeTierMaxFileBytes = 1L * 1024 * 1024 * 1024;

    public FileBoomPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — substitutes the discovery GET and the multipart upload with canned
    /// responders.</summary>
    internal FileBoomPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "FileBoom";

    public override long? MaxFileSize => FreeTierMaxFileBytes;

    protected override string ApiBase => "https://api.fboom.me/v1";

    protected override string LoginUrl => "https://fboom.me/auth/login";

    protected override string CookieDomain => ".fboom.me";

    protected override string SiteOrigin => "https://fboom.me";
}
