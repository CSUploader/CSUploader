// <copyright file="Keep2SharePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Keep2Share (keep2share.cc, short links on k2s.cc) — the second "moneyplatform" sister site,
/// running the identical <c>/v1/*</c> API as <see cref="FileBoomPipeline"/> on the same backend
/// (verified against the live site 2026-06-27: same accessToken JWT <c>aud:client→user</c> scheme,
/// the same <c>/v1/files/upload-url</c> → <c>*.filestore.app</c> multipart, and a
/// <c>/v1/users/me/statistic</c> reporting a 10 GiB free storage quota). All protocol lives in
/// <see cref="MoneyPlatformPipeline"/>; this subclass supplies only the keep2share.cc domains.
/// </summary>
public sealed class Keep2SharePipeline : MoneyPlatformPipeline
{
    public Keep2SharePipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — substitutes the discovery GET and the multipart upload with canned
    /// responders.</summary>
    internal Keep2SharePipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Keep2Share";

    // The free-tier per-file cap is unknown (the capture only carried a 5 MB file) and we're
    // deliberately NOT guessing one (decided 2026-06-27): null = no client-side per-file cap. The
    // base's pre-flight storage check still blocks any file that won't fit the 10 GiB free quota
    // before sending bytes, so over-quota files waste nothing. If a real per-file limit surfaces
    // (e.g. a server rejection, as happened for isra's 5 MiB), set it here so an under-quota-but-
    // over-cap file is rejected up front.
    public override long? MaxFileSize => null;

    protected override string ApiBase => "https://api.keep2share.cc/v1";

    // SPA login route (confirmed against the live site 2026-06-27) — the WebView opens this and
    // captures the post-login accessToken cookie.
    protected override string LoginUrl => "https://keep2share.cc/auth/login";

    protected override string CookieDomain => ".keep2share.cc";

    protected override string SiteOrigin => "https://keep2share.cc";
}
