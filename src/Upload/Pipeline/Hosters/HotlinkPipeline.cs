// <copyright file="HotlinkPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Hotlink. Standard XFileSharingPro API — the upload protocol lives in
/// <see cref="XFileSharingApiPipeline"/>; this subclass supplies only Name + Host (plus an
/// Ex-Load-style uncapped <see cref="MaxFileSize"/>). The 2026-05-29 probe sweep confirmed
/// the REST endpoints respond with the canonical <c>{status, msg, server_time}</c> shape and
/// that <c>/login.html</c> is served direct (no redirects), but a real upload has NOT been
/// verified end-to-end — treat the XFileSharing assumption as probe-confirmed, not
/// upload-proven, until a live sign-in + upload round-trips.
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

    /// <summary>
    /// Uncapped, mirroring Ex-Load (the hoster Hotlink most resembles). The base's 1 GiB
    /// default is only a conservative free-tier guess; lifted here for parity pending a real
    /// hotlink.cc free-account upload that confirms the actual limit. If oversized free
    /// uploads start getting rejected server-side, restore a concrete cap here.
    /// </summary>
    public override long? MaxFileSize => null;
}
