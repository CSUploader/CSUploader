// <copyright file="HotlinkPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Hotlink. XFileSharingPro API — the upload protocol lives in
/// <see cref="XFileSharingApiPipeline"/>. This subclass supplies Name + Host, an Ex-Load-style
/// uncapped <see cref="MaxFileSize"/>, and a non-default session-cookie name
/// (<see cref="CookieName"/> = <c>xfsts</c>). Sign-in is hCaptcha-gated and the login POST is
/// the standard <c>op=login</c> form (login capture 2026-06-23). A real upload has not yet
/// been verified end-to-end.
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
    /// hotlink.cc names its session cookie <c>xfsts</c>, not the family-default <c>xfss</c>.
    /// Verified from a login capture (2026-06-23): the <c>op=login</c> POST's 302 sets
    /// <c>Set-Cookie: xfsts=…; HttpOnly; Secure</c>, and the authenticated <c>?op=my_account</c>
    /// request carries it. Without this override the sign-in WebView watches for an <c>xfss</c>
    /// cookie that never appears, so it never detects success and never closes after login.
    /// </summary>
    protected override string CookieName => "xfsts";

    /// <summary>
    /// Uncapped, mirroring Ex-Load (the hoster Hotlink most resembles). The base's 1 GiB
    /// default is only a conservative free-tier guess; lifted here for parity pending a real
    /// hotlink.cc free-account upload that confirms the actual limit. If oversized free
    /// uploads start getting rejected server-side, restore a concrete cap here.
    /// </summary>
    public override long? MaxFileSize => null;
}
