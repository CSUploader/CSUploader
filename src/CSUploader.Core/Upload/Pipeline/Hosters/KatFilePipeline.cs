// <copyright file="KatFilePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// KatFile. Same XFileSharing API convention as <see cref="ExLoadPipeline"/> — supplying
/// only Name + Host is enough; the protocol lives in <see cref="XFileSharingApiPipeline"/>.
/// </summary>
/// <remarks>
/// <para>
/// KatFile has MIGRATED its live web + API host to <c>katfile.biz</c>. The older
/// <c>katfile.space</c> now 301-redirects the web UI to <c>katfile.biz</c>
/// (<c>katfile.space/login.html</c> → <c>katfile.biz/login.html</c>), and — critically —
/// the session cookie is minted on the redirect target: after a WebView sign-in the server
/// sets <c>Set-Cookie: xfss=…; domain=.katfile.biz</c>. So <see cref="Host"/> is
/// <c>katfile.biz</c>: <see cref="XFileSharingApiPipeline.LoginUrl"/> and the sign-in cookie
/// read must target the domain the <c>xfss</c> cookie actually lives on, and the
/// <c>/api/account/info</c> + <c>/api/upload/server</c> API is served on the same host
/// (XFileSharing convention — one host for web + API).
/// </para>
/// <para>
/// Was <c>katfile.space</c> until 2026-07-24, which broke the WebView sign-in: the completion
/// poll read cookies for <c>katfile.space</c> and never saw the <c>xfss</c> set on
/// <c>.katfile.biz</c>, so the login window never detected the session and never closed
/// (identical on the WebView2 and CefGlue heads — it is engine-agnostic Core config).
/// Diagnosed from a redacted Fiddler capture of the live login.
/// </para>
/// <para>
/// If web and API ever split across hosts, add a <c>protected virtual string ApiHost =&gt; Host</c>
/// to the base and override it here (leaving <see cref="Host"/> = the web/login host).
/// </para>
/// </remarks>
public sealed class KatFilePipeline : XFileSharingApiPipeline
{
    public KatFilePipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — same shape as <see cref="ExLoadPipeline"/>'s.</summary>
    internal KatFilePipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "KatFile";

    protected override string Host => "https://katfile.biz";
}
