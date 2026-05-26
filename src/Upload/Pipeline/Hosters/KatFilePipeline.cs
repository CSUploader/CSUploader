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
/// KatFile serves the same REST API on two domains: <c>katfile.cloud</c> (the marketed
/// API URL) and <c>katfile.space</c> (the canonical web UI host). We pick
/// <c>katfile.space</c> as <see cref="Host"/> because:
/// </para>
/// <list type="bullet">
///   <item>Both domains serve <c>/api/account/info</c> and <c>/api/upload/server</c>
///   identically, so the API-key-direct path works either way.</item>
///   <item><c>katfile.cloud/login.html</c> and <c>katfile.cloud/?op=my_account</c>
///   301-redirect to the corresponding <c>katfile.space</c> URLs. Our HTTP handler has
///   <c>AllowAutoRedirect = false</c>, so using <c>.cloud</c> as Host would break the
///   U/P bootstrap path (the my_account scrape would see an empty 301 body and the
///   regexes would find nothing).</item>
///   <item><c>katfile.space</c> serves both API and web with no redirects — single
///   host, both paths work.</item>
/// </list>
/// <para>
/// If KatFile ever turns off <c>.space</c> in favour of <c>.cloud</c>, the fix is to
/// add a <c>protected virtual string ApiHost =&gt; Host</c> to the base and override it
/// here.
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

    protected override string Host => "https://katfile.space";
}
