// <copyright file="FlashBitPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// FlashBit — <b>DISABLED 2026-06-05</b>. Standard XFileSharingPro API shape (both
/// <c>/api/account/info</c> and <c>/api/upload/server</c> respond with the canonical
/// <c>{status, msg, server_time}</c> envelope), but the upload path is unusable. Code
/// retained for the day FlashBit fixes its infrastructure; <b>do not re-enable without
/// reading the diagnosis below and re-verifying every item.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why disabled — two compounding failures on the storage tier:</b>
/// </para>
/// <list type="number">
///   <item>
///     <b>Invalid / expired SSL cert on the storage subdomain</b>. The API hands back
///     <c>https://fs1.flashbit.cc/cgi-bin/upload.cgi</c>, but <c>fs1.flashbit.cc:443</c>
///     presents a junk cert (originally observed as self-signed for
///     <c>srv1.pusula.co</c>; reconfirmed expired/invalid on 2026-06-05). TLS handshake
///     fails before the first body byte is written.
///   </item>
///   <item>
///     <b>Microsoft-IIS/10.0 request-body cap on the storage backend</b>. The
///     HTTPS→HTTP scheme-downgrade workaround (commit 725ffba) gets us past TLS, but
///     IIS then 413s our chunked POSTs (the 80 MiB hxfile-shape chunk is way over the
///     default <c>maxAllowedContentLength</c> of ~28.6 MiB). The probe-and-shrink
///     retry at 20 MiB also 413s in some cases, and the classic single-multipart fallback
///     POSTs the entire file as one body — same cap, same rejection.
///   </item>
/// </list>
/// <para>
/// <b>Re-enable checklist</b> (all four touchpoints + verification, in order):
/// </para>
/// <list type="number">
///   <item>Confirm <c>openssl s_client -connect fs1.flashbit.cc:443</c> returns a valid,
///   non-expired cert chain whose CN matches <c>flashbit.cc</c> (or a wildcard for it).</item>
///   <item>Capture a successful upload from the live <c>flashbit.cc</c> web UI to
///   identify the actual chunk size their browser uses, then either confirm it matches
///   our existing initial / fallback constants or parameterise per-hoster.</item>
///   <item>Uncomment the DI registration in <c>App.xaml.cs</c>.</item>
///   <item>Uncomment the <c>"FlashBit", "flashbit.cc"</c> entry in
///   <c>FileHosterClient.FileHosters</c>.</item>
///   <item>Add <c>"FlashBit"</c> back to <c>EditAccountWindow.ApiKeyHosters</c>.</item>
///   <item>Flip the smoke test's registry-presence assertion back to <c>Assert.True</c>.</item>
/// </list>
/// <para>
/// See the <c>xfs-chunked-upload-protocol</c> memory for the wire shape and prior
/// arc, and commit 725ffba for the HTTPS→HTTP downgrade workaround.
/// </para>
/// </remarks>
public sealed class FlashBitPipeline : XFileSharingApiPipeline
{
    public FlashBitPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    internal FlashBitPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "FlashBit";

    protected override string Host => "https://flashbit.cc";

    /// <summary>
    /// FlashBit's storage subdomain (<c>fs1.flashbit.cc</c>) serves a junk cert on :443 but
    /// HTTP/1.1 cleanly on :80, so the https upload URL the API returns must be downgraded to
    /// http (the original 725ffba workaround — see class remarks, item 1). This is the only
    /// hoster that needs it; the base default respects the API's scheme. (FlashBit is disabled,
    /// so this only matters if it's ever re-enabled.)
    /// </summary>
    protected override bool DowngradeUploadServerToHttp => true;
}
