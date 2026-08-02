// <copyright file="FileaxaPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// FILEAXA (fileaxa.com) — account upload on the standard XFileSharing REST API, so this is the
/// thinnest kind of shim: a name, a host, and one cap decision. The protocol lives in
/// <see cref="XFileSharingApiPipeline"/>.
/// <para>
/// Verified live 2026-08-02: <c>/api/account/info?key=</c> and <c>/api/upload/server?key=</c> both
/// answer the family's <c>{"status":400,"msg":"Invalid key"}</c>, and — unlike the Uploadrar fork —
/// the family-default login page <c>/login.html</c> serves a 200, so no route overrides are needed.
/// </para>
/// <para>
/// <b>Account-only.</b> Its homepage renders no <c>utype=anon</c> / <c>upload.cgi</c> form (checked
/// 2026-08-02, and again in the tier-A1 sweep), so there is no anonymous path to offer.
/// </para>
/// <para>
/// <b>No declared cap.</b> The candidate list records "free upload to 10000 MB", but nothing on the
/// host states it where this pipeline can read it: <c>?op=api_get_limits</c> answers with the
/// homepage rather than the family's XML, so there is no <c>MaxUploadFilesize</c> to trust. Encoding
/// the unverified figure would reject files the server might well accept, so
/// <see cref="MaxFileSize"/> is null and the server's own refusal is the authority.
/// </para>
/// </summary>
public sealed class FileaxaPipeline : XFileSharingApiPipeline
{
    public FileaxaPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — same shape as the other XFS shims'.</summary>
    internal FileaxaPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "FILEAXA";

    protected override string Host => "https://fileaxa.com";

    /// <summary>
    /// Null, not the base's 1 GiB default — which would silently skip anything larger at queue time
    /// on a host advertised at ~10 GB. See the class remarks for why the advertised figure isn't
    /// encoded either.
    /// </summary>
    public override long? MaxFileSize => null;
}
