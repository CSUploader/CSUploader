// <copyright file="HotlinkPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Hotlink. Standard XFileSharingPro API — verified end-to-end during the 2026-05-29
/// probe sweep (both REST endpoints responding with the canonical
/// <c>{status, msg, server_time}</c> shape; <c>/login.html</c> served direct, no
/// redirects). Only Name + Host required; protocol lives in
/// <see cref="XFileSharingApiPipeline"/>.
/// </summary>
public sealed class HotlinkPipeline : XFileSharingApiPipeline
{
    public HotlinkPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    internal HotlinkPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Hotlink";

    protected override string Host => "https://hotlink.cc";
}
