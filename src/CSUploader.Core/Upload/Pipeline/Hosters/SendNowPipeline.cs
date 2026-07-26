// <copyright file="SendNowPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Send.now — classic XFileSharing; the protocol lives in <see cref="XFileSharingApiPipeline"/>.
/// <para>
/// Formerly <b>send.cm</b> (and tusfiles / sendit before that): send.cm now 301s to send.now, so the
/// live brand is the one wired here — a single entry covers traffic addressed to either.
/// </para>
/// <para>
/// Probed live 2026-07-26: the homepage renders the family's anonymous form
/// (<c>&lt;form action="https://dlNNNN.send.now/cgi-bin/upload.cgi?upload_type=file&amp;utype=anon"&gt;</c>
/// with an empty <c>sess_id</c>), so the base's anonymous path applies unchanged. It is genuinely
/// stock XFS: <c>?op=api_get_limits</c> answers with the standard
/// <c>&lt;Data&gt;…&lt;ServerURL&gt;…</c> XML, and <c>/api/upload/server</c> hands out a node. Cloudflare
/// is passive (plain GETs succeed from the C# stack).
/// </para>
/// </summary>
public sealed class SendNowPipeline : XFileSharingApiPipeline
{
    public SendNowPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow
    /// from canned responses.</summary>
    internal SendNowPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Send.now";

    protected override string Host => "https://send.now";

    /// <summary>Anonymous (not-logged-in) upload verified against the live homepage form.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>
    /// No client-side per-file cap: the host's own <c>?op=api_get_limits</c> reports
    /// <c>&lt;MaxUploadFilesize&gt;0&lt;/MaxUploadFilesize&gt;</c> — the XFileSharing convention for
    /// "unlimited" — while the marketing page advertises multi-GB uploads. Rather than invent a
    /// number that could reject a file the server would have accepted, let the server be the gate;
    /// it rejects an over-limit upload up front (no wasted bytes) the way the other uncapped
    /// anonymous hosters do.
    /// </summary>
    public override long? MaxFileSize => null;
}
