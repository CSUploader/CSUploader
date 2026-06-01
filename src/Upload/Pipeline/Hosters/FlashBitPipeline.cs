// <copyright file="FlashBitPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// FlashBit. Standard XFileSharingPro API — both <c>/api/account/info</c> and
/// <c>/api/upload/server</c> were confirmed responding with the canonical
/// <c>{status, msg, server_time}</c> shape during the 2026-05-26 probe sweep. Only
/// Name + Host needed; protocol lives in <see cref="XFileSharingApiPipeline"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Currently DISABLED — not registered in DI, not in the FileHosters registry, not
/// in EditAccountWindow.ApiKeyHosters.</b> The class is kept intact so re-enabling is a
/// 3-line restoration (DI + FileHosters + ApiKeyHosters), no re-derivation of config.
/// </para>
/// <para>
/// <b>Why disabled (2026-06-01):</b> the upload flow fails end-to-end against the live
/// service through two stacked issues:
/// </para>
/// <list type="number">
///   <item>The API returns <c>https://fs1.flashbit.cc/cgi-bin/upload.cgi</c> for the
///   per-user storage subdomain, but :443 on <c>fs1.flashbit.cc</c> presents a
///   self-signed certificate issued for an unrelated CN (<c>srv1.pusula.co</c>), so
///   the .NET handler rejects the connection before any bytes ship. We
///   landed a generic fix for this in
///   <see cref="XFileSharingApiPipeline"/>'s <c>NormaliseUploadUrlScheme</c> — when
///   the upload URL points at a host different from the API host, the scheme is
///   downgraded to <c>http</c>. That stopped the TLS handshake error.</item>
///   <item>After the downgrade, the storage server closes the connection mid-stream
///   while we're sending the multipart body (<c>SocketException 10054</c> from
///   <c>ProgressStreamContent.SerializeToStreamAsync</c>). This is the same
///   wire-shape-rejection signature BRupload exhibited before commits
///   <c>bfd2aec</c> / <c>d59e5df</c> / <c>891041d</c> — the server actively closes
///   when something about the multipart preamble doesn't match what its browser
///   client sends. Diagnosing requires a working-browser capture to byte-diff against
///   (see the <c>brupload-multipart-quirks</c> memory for the methodology); without
///   that we don't know which axis FlashBit is enforcing.</item>
/// </list>
/// <para>
/// <b>Re-enable checklist:</b>
/// </para>
/// <list type="bullet">
///   <item>Obtain a Fiddler/devtools capture of a successful upload from a browser
///   (request headers + first ~3 KB of the multipart body).</item>
///   <item>Diff against what we send. The five usual suspects (boundary unquoting,
///   <c>name=</c> quoting, <c>filename*=</c> for non-ASCII only, real MIME on the file
///   part, Origin + Sec-Fetch-*) are already correctly shaped in
///   <see cref="HttpHandler.UploadMultipartAsync"/>; the gap is something else.</item>
///   <item>Once fixed, restore three call sites in lockstep: the
///   <c>AddSingleton&lt;IFileHosterPipeline, FlashBitPipeline&gt;</c> line in
///   <c>App.xaml.cs</c>, the <c>{ "FlashBit", "flashbit.cc" }</c> entry in
///   <c>FileHosterClient.FileHosters</c>, and the <c>"FlashBit"</c> entry in
///   <c>EditAccountWindow.ApiKeyHosters</c>. The
///   <c>Name_IsNotRegistered_WhileDisabled</c> smoke test below will fail on
///   re-enable — flip it back to <c>Assert.True</c> at that point.</item>
/// </list>
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
}
