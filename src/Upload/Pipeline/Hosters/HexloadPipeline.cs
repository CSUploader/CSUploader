// <copyright file="HexloadPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Hexload. Standard XFileSharingPro API — both <c>/api/account/info</c> and
/// <c>/api/upload/server</c> confirmed responding with the canonical
/// <c>{status, msg, server_time}</c> shape during the 2026-05-29 probe sweep.
/// <c>/login.html</c> is direct (no redirects) so the U/P bootstrap WebView lands on
/// the right page.
/// </summary>
/// <remarks>
/// <c>hexupload.net</c> is an alias of <c>hexload.com</c> — both the web UI and API
/// endpoints 301 from <c>.net</c> to <c>.com</c>. We register only Hexload (under the
/// <c>.com</c> host), which transparently covers traffic addressed to either domain.
/// </remarks>
public sealed class HexloadPipeline : XFileSharingApiPipeline
{
    public HexloadPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    internal HexloadPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Hexload";

    protected override string Host => "https://hexload.com";
}
