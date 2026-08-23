// <copyright file="DataVaultsPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Data Vaults (datavaults.co) — XFileSharing on the base's <b>REST API</b> path, so the whole host
/// is a name, a host and a cap.
/// <para>
/// <b>Choosing the API here is the opposite call to filedot.to's, and the evidence is why.</b> A
/// browser capture of a signed-in upload (2026-08-02) shows the site's own client using the xfspro
/// chunked route — <c>GET /server</c> → <c>put_chunk.cgi</c> + <c>X-Upload-SID</c> → multipart
/// <c>api.cgi op=import_file</c> — which this app would have to grow session support for. But unlike
/// FILEAXA (whose API existed and was used by nothing) this host <b>publishes API documentation</b> at
/// <c>/pages/api</c> describing exactly the flow <see cref="XFileSharingApiPipeline"/> already
/// implements:
/// <list type="number">
///   <item><c>GET /api/upload/server?key=KEY</c> → <c>{status, sess_id, result: "…/cgi-bin/upload.cgi"}</c></item>
///   <item>multipart POST to that URL with <c>sess_id</c> → <c>[{"file_code":"…","file_status":"OK"}]</c></item>
///   <item>link <c>datavaults.co/&lt;code&gt;</c></item>
/// </list>
/// …and <c>upload.cgi</c> on a live node was confirmed to exist and process (it answers the family
/// JSON, not a 404). A documented, key-issuing API is a supported product; a bare endpoint answering
/// "Invalid key" is not — that is the distinction FILEAXA taught, read the other way round.
/// </para>
/// <para>
/// <b>The key is user-obtainable in one click</b>, which is what makes this shippable where DDownload
/// wasn't: My Account carries a "Generate API Key" link. The base already automates precisely that —
/// when <c>?op=my_account</c> shows no key it re-requests with
/// <c>&amp;generate_api_key=1&amp;token=&lt;csrf&gt;</c> and re-reads — so WebView sign-in should
/// derive a key unaided, and a user who already has one can paste it instead.
/// </para>
/// <para>
/// <b>Account-only.</b> An anonymous <c>import_file</c> answers <c>uploads are not enabled for your
/// account type</c>. Note its anonymous <c>upload.cgi</c> instead answers
/// <c>[{"file_status":"OK","file_code":"undef"}]</c> — a success shape for an upload it threw away.
/// The base now rejects <c>undef</c> outright; without that guard this host would have reported
/// <c>datavaults.co/undef</c> as a finished upload.
/// </para>
/// <para>
/// Routes are pretty (<c>/upload/</c>, <c>/account/</c>) but the <c>?op=</c> forms still work — the
/// family's <c>?op=my_account</c> answers, redirecting to <c>/login.html</c> when signed out, which is
/// also why the family's default login path needs no override here.
/// </para>
/// </summary>
public sealed class DataVaultsPipeline : XFileSharingApiPipeline
{
    public DataVaultsPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — drives the page GETs and the multipart upload from canned responses.</summary>
    internal DataVaultsPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "DataVaults";

    /// <summary>Free downloads are captcha-gated: its premium page's "Downloads Captcha" row
    /// marks yes for Anonymous and Registered, no for Premium (premium.html,
    /// 2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.Required;

    /// <summary>From its own premium.html (read 2026-08-12): anonymous "3 Days", registered
    /// "7 Days", premium "Never". Unlike its siblings the page does NOT say what the days count
    /// from, so this reports the floor - from upload - rather than promising the last-download
    /// reset its siblings give.</summary>
    public override FileRetention RetentionFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? FileRetention.DaysAfterUpload(3)
            : credentials.AccountType == AccountType.Premium ? FileRetention.Permanent
            : FileRetention.DaysAfterUpload(7);

    protected override string Host => "https://datavaults.co";

    /// <summary>
    /// 5 GB per file for a registered account, from the signed-in uploader's own line ("Max file size
    /// is 5120 Mb"); the signed-out page advertises 1024 Mb, which is moot since anonymous uploads are
    /// refused. Read as binary — this family's limits are 1024-based.
    /// <para>
    /// Storage is NOT unlimited, whatever the homepage says. Its API doc's example prints
    /// <c>storage_left: "inf"</c>, but a live free account reports a real number — about 10 GB —
    /// which the base turns into a quota (used + left). Both shapes are handled; the figure comes from
    /// the account, not from here.
    /// </para>
    /// </summary>
    public override long? MaxFileSize => 5120L * 1024 * 1024;

    /// <summary>
    /// <b>Four.</b> Its origin serves exactly four concurrent API requests and drops the rest —
    /// measured 2026-08-02 by holding everything else constant and varying only concurrency:
    /// <list type="bullet">
    ///   <item>sequential ×6 → six 200s;</item>
    ///   <item>concurrency 2, 3, 4 → all 200, repeatedly;</item>
    ///   <item>concurrency 5 → <b>four 200s and one 520, on both runs</b>;</item>
    ///   <item>concurrency 6 and 8 → a 520 each.</item>
    /// </list>
    /// Cloudflare returns that as <c>520</c> with the body <c>error code: 520</c>, which reads like a
    /// site outage and is really a queue depth. The user who reported it guessed "maybe 4" from
    /// watching the wizard, which is exactly what the measurement says.
    /// <para>
    /// The lookup also retries an unreadable answer (see
    /// <c>XFileSharingApiPipeline.GetUploadServerAsync</c>), but that is the safety net, not the fix:
    /// retrying into a limit that persists for as long as the batch runs just spends the retries. The
    /// cure is not to exceed it, so the scheduler caps this host at four uploads at once — the same
    /// arrangement Send.now needs.
    /// </para>
    /// </summary>
    public override int? MaxConcurrentUploadsFor(FileHosterLoginDto credentials) => 4;
}
