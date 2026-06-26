// <copyright file="IsraCloudPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Isracloud (isra.cloud). A classic XFileSharing host that does NOT expose the per-account REST
/// API — its <c>my_account</c> page renders no <c>api-url</c>/key (verified end-to-end from a fresh
/// free account, Fiddler capture 2026-06-26). So it runs the base's web-form path instead of the
/// API path:
/// <list type="number">
///   <item><b>Sign-in.</b> WebView login at <c>/login.html</c> captures the <c>xfss</c> session
///   cookie (the family default cookie name + <c>.isra.cloud</c> domain).</item>
///   <item><b>Upload.</b> GET the logged-in <c>?op=upload_form</c> page, scrape the form
///   <c>action</c> (<c>fsNN.isra.cloud/cgi-bin/upload.cgi?upload_type=file&amp;utype=reg</c>) + the
///   hidden <c>sess_id</c>, then post the file as a classic single-multipart upload →
///   <c>[{"file_code":"&lt;code&gt;","file_status":"OK"}]</c>, link <c>https://isra.cloud/&lt;code&gt;</c>.</item>
///   <item><b>Account / storage.</b> Scraped from <c>my_account</c> — username + "Used space"; no
///   quota is shown, so Available renders "Unlimited".</item>
/// </list>
/// The credential is the session cookie (no API key). The captured successful upload omits the
/// <c>upload</c> button and sends an empty <c>file_public</c>, so <see cref="BuildClassicExtraFields"/>
/// is overridden to replicate the proven set verbatim.
/// </summary>
public sealed class IsraCloudPipeline : XFileSharingApiPipeline, IStorageRefreshablePipeline
{
    public IsraCloudPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — same shape as <see cref="KatFilePipeline"/>'s; drives the GETs
    /// (<c>?op=upload_form</c> / <c>my_account</c>) and the upload from canned responses.</summary>
    internal IsraCloudPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Isracloud";

    protected override string Host => "https://isra.cloud";

    /// <summary>Web-form (no-API) hoster — see <see cref="XFileSharingApiPipeline.UsesWebFormUpload"/>.</summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>No advertised free-tier per-file cap (the capture didn't expose one), and per the
    /// "never waste an upload" rule we don't guess one: an oversized file surfaces the server's own
    /// rejection rather than a client-side block. (Confirmed 2026-06-26.)</summary>
    public override long? MaxFileSize => null;

    /// <summary>
    /// The exact field set isra.cloud's web uploader posts on a SUCCESSFUL upload (Fiddler capture
    /// 2026-06-26): no <c>upload</c> button and an empty <c>file_public</c>, unlike the family
    /// default. The XFileSharing multipart parser is field-presence/value sensitive, so we replicate
    /// the proven set verbatim rather than risk a wasted upload.
    /// </summary>
    protected override Dictionary<string, string> BuildClassicExtraFields(string sessId) => new(StringComparer.Ordinal)
    {
        ["sess_id"] = sessId,
        ["utype"] = "reg",
        ["file_descr"] = string.Empty,
        ["file_public"] = string.Empty,
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
        ["keepalive"] = "1",
    };

    /// <summary>
    /// Non-interactive storage refresh for the wizard Summary page: scrapes <c>my_account</c>'s
    /// "Used space" with the stored <c>xfss</c> cookie (never a WebView). Delegates to the base
    /// helper, which returns null when there's no usable stored session.
    /// </summary>
    public Task<StorageUsage?> RefreshStorageAsync(FileHosterLoginDto credentials, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
        => RefreshStorageViaMyAccountAsync(credentials, handler, proxy, ct);
}
