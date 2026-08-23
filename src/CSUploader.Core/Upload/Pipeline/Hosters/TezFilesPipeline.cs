// <copyright file="TezFilesPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// TezFiles (tezfiles.com) — the third "moneyplatform" sister site, running the identical
/// <c>/v1/*</c> API as <see cref="FileBoomPipeline"/> / <see cref="Keep2SharePipeline"/> on the same
/// backend (verified from a 2026-06-28 capture: WebView accessToken sign-in at
/// <c>tezfiles.com/auth/login</c>, <c>POST /v1/auth/token</c>, <c>GET /v1/files/upload-url</c> →
/// <c>*.filestore.app/upload</c> multipart whose signed params carry
/// <c>project:"moneyplatform", project_name:"tz"</c>, and a <c>tezfiles.com/file/&lt;id&gt;</c>
/// share link). All protocol lives in <see cref="MoneyPlatformPipeline"/>; this subclass supplies
/// only the tezfiles.com domains and the free-tier per-file cap.
/// </summary>
public sealed class TezFilesPipeline : MoneyPlatformPipeline
{
    /// <summary>TezFiles free-tier per-file ceiling — 5 GiB (reported by the account owner). The
    /// base's pre-flight storage-quota check still guards over-quota files before any bytes ship.</summary>
    private const long FreeTierMaxFileBytes = 5L * 1024 * 1024 * 1024;

    public TezFilesPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — substitutes the discovery GET and the multipart upload with canned
    /// responders.</summary>
    internal TezFilesPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "TezFiles";

    public override long? MaxFileSize => FreeTierMaxFileBytes;

    protected override string ApiBase => "https://api.tezfiles.com/v1";

    protected override string LoginUrl => "https://tezfiles.com/auth/login";

    protected override string CookieDomain => ".tezfiles.com";

    protected override string SiteOrigin => "https://tezfiles.com";
}
