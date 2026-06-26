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

    // TODO(pending): the free-tier per-file cap isn't confirmed yet (the capture only carried a 5 MB
    // test file). null = no client-side per-file cap; the base's pre-flight storage check still
    // blocks any file that won't fit the 10 GiB free quota before sending bytes. Set this to the real
    // per-file limit once known, so a file under-quota but over the per-file cap is rejected up front.
    public override long? MaxFileSize => null;

    protected override string ApiBase => "https://api.keep2share.cc/v1";

    protected override string LoginUrl => "https://keep2share.cc/auth/login";

    protected override string CookieDomain => ".keep2share.cc";

    protected override string SiteOrigin => "https://keep2share.cc";
}
