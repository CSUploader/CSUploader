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
/// Was temporarily disabled (2026-06-01 → 2026-06-02) while we diagnosed an
/// upload-path failure: the live storage backend force-closed the connection
/// mid-stream when we sent a single multipart <c>upload.cgi</c> POST. The HxFile
/// browser capture on 2026-06-02 revealed the modern XFileSharing CDN protocol is a
/// chunked flow (<c>up.cgi</c> per chunk + <c>api.cgi</c> finalize); the legacy
/// single-multipart shape is rejected by the CDN frontends. Once the chunked path
/// landed in <see cref="XFileSharingApiPipeline"/>'s router (chunked-first with a
/// classic fallback), FlashBit was re-enabled. See the
/// <c>xfs-chunked-upload-protocol</c> memory for the wire shape.
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
}
