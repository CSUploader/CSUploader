// <copyright file="DDownloadPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// DDownload (ddownload.com, ex ddl.to) — XFileSharing <b>Pro</b> on the base's API-key path, and the
/// thinnest shim in the family: everything below this comment is configuration, because the protocol is
/// the base's already. Verified end-to-end against a live FREE account 2026-08-01 (upload accepted,
/// link served, file deleted again afterwards).
/// <list type="number">
///   <item><c>GET /api/upload/server?key=KEY</c> → <c>{"result":"https://NODE/cgi-bin/upload.cgi",
///   "sess_id":"…","status":200}</c> — a 77-character API session, not the <c>xfss</c> cookie.</item>
///   <item>Classic multipart POST to <c>result</c> with that <c>sess_id</c> and the family's default
///   nine fields, unaltered → <c>[{"file_code":"…","file_status":"OK"}]</c>.</item>
///   <item>Link is <c>ddownload.com/&lt;code&gt;</c>, the family default.</item>
/// </list>
/// <para>
/// <b>Its one genuine deviation is WHERE the API lives:</b> only <c>api-v2.ddownload.com</c> answers
/// <c>/api/*</c> — the main host returns an HTML page — while links, <c>my_account</c> and sign-in stay
/// on <c>ddownload.com</c>. Hence <see cref="ApiHost"/>, which exists for this.
/// </para>
/// <para>
/// <b>Free accounts CAN upload here</b>, which is worth stating because it is not the norm any more:
/// DropGalaxy, Uploady and Clicknupload all advertised guest or free uploads and refused them in
/// practice. This one was proven by uploading a real file with a free account's key.
/// </para>
/// <para>
/// <b>No per-file cap is declared</b> — the host publishes no figure anywhere (no
/// <c>?op=api_get_limits</c>, and <c>/api/upload/limits</c> answers "Invalid operation"), and its
/// <c>/api/account/info</c> reports <c>storage_mode: "unlimited"</c> with <c>storage_left: "inf"</c>.
/// Rather than encode a guess that would silently reject good files, <see cref="MaxFileSize"/> stays
/// null and the server's own refusal is the authority. Revisit if a real upload ever comes back
/// rejected on size.
/// </para>
/// <para>
/// Credentials: the API key. The base can bootstrap one by scraping <c>?op=my_account</c> after a
/// WebView sign-in, and the account dialog also accepts a key pasted directly — which is the quicker
/// route here, since the account page shows it.
/// </para>
/// </summary>
public sealed class DDownloadPipeline : XFileSharingApiPipeline
{
    public DDownloadPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow from canned
    /// responses.</summary>
    internal DDownloadPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "DDownload";

    protected override string Host => "https://ddownload.com";

    /// <summary>The API is served ONLY from this host; <c>ddownload.com/api/*</c> returns HTML.</summary>
    protected override string ApiHost => "https://api-v2.ddownload.com";

    /// <summary>
    /// No cap enforced client-side: the host declares none, and a wrong guess is the expensive kind of
    /// wrong — it would reject files the server would have taken. See the class remarks.
    /// </summary>
    public override long? MaxFileSize => null;
}
