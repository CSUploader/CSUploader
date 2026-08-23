// <copyright file="HotlinkPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Hotlink (hotlink.cc) — <b>DISABLED 2026-06-23</b>: free accounts cannot upload and the
/// per-user API key is unobtainable. The class is retained (with the full diagnosis below) so
/// re-enabling is cheap IF an upload-enabled account ever exists — but it needs a different
/// upload path than the rest of the XFileSharing-API family, so do NOT just uncomment it.
/// </summary>
/// <remarks>
/// <para><b>Why disabled</b> (verified live + against the XFileSharing Pro source the user purchased):</para>
/// <list type="number">
///   <item><b>Free can't upload.</b> Logged in, <c>/?op=upload</c> redirects to <c>upload_file</c>,
///   which renders "You are not allowed to upload files" — hotlink's fork disables registered-tier
///   uploads (premium-only). It is also a <b>video-only</b> host (<c>api_get_limits</c> ExtAllowed =
///   avi/mkv/mp4/… only) with a ~100 MB free cap (<c>MaxUploadFilesize=100000</c>).</item>
///   <item><b>The API key can't be obtained.</b> XFileSharing Pro's per-user "api-url" key is never
///   rendered on <c>?op=my_account</c> (the display fragment is gated off on hotlink's skin).
///   <c>generate_api_key=1</c> flips a DB row ("New API key generated" flash) but the value is never
///   echoed anywhere, and no op/endpoint returns a per-user key from a session. So the base's
///   api-key bootstrap (scrape <c>api-url</c> from my_account) is structurally impossible here.</item>
/// </list>
/// <para><b>The REAL upload path</b> (for the re-enable day, with an upload-enabled account):
/// XFileSharing's <c>upload.cgi</c> authenticates by <c>sess_id</c> ONLY — never an api key — and
/// <c>sess_id</c> == the session-cookie value (here <see cref="CookieName"/> = <c>xfsts</c>).
/// Discover the upload server via <c>GET /?op=api_get_limits&amp;session_id=&lt;xfsts&gt;</c>
/// (returns <c>&lt;ServerURL&gt;</c>, e.g. <c>https://enc1.hotlink.cc/cgi-bin</c>), then POST
/// multipart to <c>&lt;ServerURL&gt;/upload.cgi?upload_type=file</c> with <c>sess_id=&lt;xfsts&gt;</c>
/// + <c>utype=reg</c> (or <c>prem</c>) + <c>file_0</c>. Response <c>[{file_code, file_status:"OK"}]</c>;
/// link <c>https://hotlink.cc/&lt;code&gt;</c>. This is a LOGGED-IN web upload (like the base's
/// anonymous path but with a real <c>sess_id</c> + non-anon <c>utype</c>), NOT the api-key path.
/// <b>CRITICAL:</b> a wrong/expired <c>sess_id</c> still "succeeds" but stores the file as
/// ANONYMOUS (<c>usr_id=0</c>) with no error — any re-enable MUST verify the upload is attributed
/// to the account.</para>
/// <para><b>Re-enable checklist:</b> needs an upload-enabled (premium) account; implement the
/// logged-in web-upload mode above; then flip all four touchpoints — <c>FileHosterClient.FileHosters</c>,
/// the <c>App.xaml.cs</c> DI registration, the <c>EditAccountWindow</c> ApiKeyHosters set, and the
/// <c>HotlinkPipelineSmokeTests</c> "not registered" sentinel. The <see cref="CookieName"/> = <c>xfsts</c>
/// override is correct and verified — keep it.</para>
/// </remarks>
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
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
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
    /// Moot while the hoster is DISABLED (see class remarks). If ever re-enabled, the real
    /// free-tier cap is ~100 MB (<c>api_get_limits</c> <c>MaxUploadFilesize=100000</c>) and uploads
    /// are video-only — this <c>null</c> would need to become that concrete cap.
    /// </summary>
    public override long? MaxFileSize => null;
}
