// <copyright file="ExLoadPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Ex-Load. Pure config — the upload protocol lives in
/// <see cref="XFileSharingApiPipeline"/>. To add another XFileSharing-API hoster (if its
/// API matches this convention), subclass the base with a similar two-line shim and
/// register in DI.
/// </summary>
public sealed class ExLoadPipeline : XFileSharingApiPipeline
{
    public ExLoadPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so existing test fixtures
    /// can continue to drive ExLoadPipeline through canned responses.</summary>
    internal ExLoadPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Ex-Load";

    protected override string Host => "https://ex-load.com";

    /// <summary>
    /// Ex-Load's 100 MB per-file cap binds ONLY for anonymous guest uploads. Logged-in
    /// members (which our pipeline always is — anon uploads aren't supported on this
    /// hoster path) have no documented per-file cap; the user's account dashboard
    /// confirms unlimited uploads.
    /// </summary>
    public override long? MaxFileSize => null;

    // Storage usage (storage_used / storage_left) is surfaced by the base from the
    // /api/account/info JSON response — no per-subclass HTML scrape needed. Ex-Load's
    // storage_left is "inf", so the base leaves the quota null and the grid's Available
    // cell renders blank while Used shows the byte count.
}
