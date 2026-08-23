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
/// </summary>
/// <remarks>
/// DISABLED 2026-07-26, the day it was added — the class is retained (registry entry, DI
/// registration and the EditAccount ApiKeyHosters entry are all commented out; the smoke test
/// asserts it is absent from the registry) so a re-enable is low-churn.
/// <para>
/// Why: <b>anonymous uploads are capped at 0.00001 MB</b> — about ten bytes. The homepage's
/// <c>max_upload_filesize: '0.00001'</c> was visible during the build probe but read as a template
/// placeholder; a live upload attempt returned the host's own message, "File size limit is 0.00001
/// Mbytes", confirming the figure is real and enforced. No file this app uploads can fit.
/// </para>
/// <para>
/// And the account path can't rescue it: <b>registration is closed</b>, so there is no way to obtain
/// the API key the family's REST path needs. (The endpoint itself is alive — <c>/api/upload/server</c>
/// answers <c>{"msg":"Invalid key"}</c> to a keyless call — it just can't be reached without an account.)
/// </para>
/// <para>
/// The protocol wiring below is correct and was verified against the live site: the homepage renders
/// the family's anonymous form verbatim (<c>&lt;form id="uploadfile"
/// action="https://dg.a2zupload.com/cgi-bin/upload.cgi?upload_type=file&amp;utype=anon"&gt;</c>, the same
/// shape Hexload's does, with the upload node on a separate <c>a2zupload.com</c> domain), and
/// Cloudflare is passive. Only the caps make it useless.
/// </para>
/// <para>
/// Re-enable checklist: confirm the anonymous cap is a usable size (or that registration reopened and
/// yields an API key), then un-comment (1) the registry entry in <c>FileHosterClient</c>, (2) the DI
/// registration in <c>ServiceRegistration</c>, (3) the <c>ApiKeyHosters</c> entry in
/// <c>HosterCredentialModes</c>, and (4) flip the absence assertion in the tests. The icon + PNG are
/// retained, so nothing else is needed.
/// </para>
/// </remarks>
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
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
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
