// <copyright file="DropGalaxyPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// DropGalaxy — classic XFileSharing; the protocol lives in <see cref="XFileSharingApiPipeline"/>.
/// <para>
/// Probed live 2026-07-26: the homepage renders the family's anonymous form verbatim
/// (<c>&lt;form id="uploadfile" action="https://dg.a2zupload.com/cgi-bin/upload.cgi?upload_type=file&amp;utype=anon"&gt;</c>)
/// — the same shape Hexload's does, so the base's anonymous path applies unchanged. Note the upload
/// node lives on a SEPARATE domain (<c>a2zupload.com</c>); that is fine, the POST goes wherever the
/// scraped action points. Cloudflare is passive. Its accounts speak the standard REST API
/// (<c>/api/upload/server</c> answers <c>{"msg":"Invalid key"}</c> to a keyless call, i.e. the
/// endpoint exists and validates keys).
/// </para>
/// </summary>
public sealed class DropGalaxyPipeline : XFileSharingApiPipeline
{
    public DropGalaxyPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow
    /// from canned responses.</summary>
    internal DropGalaxyPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "DropGalaxy";

    protected override string Host => "https://dropgalaxy.com";

    /// <summary>Anonymous (not-logged-in) upload verified against the live homepage form.</summary>
    public override bool SupportsAnonymousUpload => true;

    // MaxFileSize deliberately left at the base's 1 GiB free-tier default. Unlike its siblings,
    // DropGalaxy does NOT answer ?op=api_get_limits with the standard XML (it returns the ordinary
    // HTML page), so there is no authoritative figure to read; published free-tier figures sit around
    // 1-2 GB. The conservative default only ever rejects EARLY — the failure mode a too-generous cap
    // produces (a multi-GB upload that the server refuses at the end) is the expensive one.
    // Raise this once a real over-1-GiB anonymous upload is observed to succeed.
    //
    // Retention note: anonymously-uploaded DropGalaxy files are removed a day after their last
    // download (the host's free-tier rule) — nothing in the pipeline depends on it, but it explains
    // a link that later goes dead.
}
