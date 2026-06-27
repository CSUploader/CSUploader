// <copyright file="TakeFilePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// TakeFile. Standard XFileSharingPro API — confirmed via the 2026-05-26 probe sweep
/// after sending a realistic browser User-Agent (TakeFile is behind a Cloudflare
/// challenge that rejects bare API clients; the
/// <see cref="Lib.Net.Http.DefaultHttpHandlerFactory.DefaultUserAgent"/> already in
/// place satisfies it). Only Name + Host needed; protocol lives in
/// <see cref="XFileSharingApiPipeline"/>.
/// </summary>
public sealed class TakeFilePipeline : XFileSharingApiPipeline
{
    public TakeFilePipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    internal TakeFilePipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "TakeFile";

    protected override string Host => "https://takefile.link";

    /// <summary>TakeFile's sign-in page is <c>/user_login</c>, not the XFS-default
    /// <c>/login.html</c> — so the WebView sign-in lands on the right page.</summary>
    protected override string LoginPagePath => "/user_login";
}
