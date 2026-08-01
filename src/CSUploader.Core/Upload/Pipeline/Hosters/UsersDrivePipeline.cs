// <copyright file="UsersDrivePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// UsersDrive (usersdrive.com) — classic XFileSharing with a LIVE anonymous upload, verified against
/// the real site 2026-08-01 by uploading a file: the homepage renders
/// <c>&lt;form id="uploadfile" action="https://dNNN.userdrive.org/cgi-bin/upload.cgi?upload_type=file&amp;utype=anon"&gt;</c>
/// with an empty <c>sess_id</c>, and posting the family's anonymous field set to it answers
/// <c>[{"file_code":"…","file_status":"OK"}]</c>. The share link is <c>usersdrive.com/&lt;code&gt;</c>
/// (checked: it serves a real download page showing the uploaded filename).
/// <para>
/// Everything else is the base's: the homepage IS the form page, so
/// <see cref="XFileSharingApiPipeline.BuildAnonUploadFormUrl"/> needs no override, and the upload
/// nodes rotate on their own domain (<c>userdrive.org</c>) which the scraped action already carries.
/// No Cloudflare challenge.
/// </para>
/// <para>
/// <b>Worth recording how this was found</b>, because the candidate doc had it filed as account-only:
/// a 2026-07-31 sweep concluded the anonymous-XFS category was closed, but that sweep only covered
/// hosts the doc already claimed were anonymous. UsersDrive was never checked — and it is anonymous.
/// A host's listed tier is a hypothesis, not a fact.
/// </para>
/// </summary>
public sealed class UsersDrivePipeline : XFileSharingApiPipeline
{
    public UsersDrivePipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow from
    /// canned responses.</summary>
    internal UsersDrivePipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "UsersDrive";

    protected override string Host => "https://usersdrive.com";

    /// <summary>Anonymous upload verified by actually uploading a file — not merely by the form being
    /// rendered, which is what DropGalaxy, Uploady and Clicknupload each had while refusing the bytes.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>
    /// 5250 MB, the figure the upload page states itself ("Max file size is 5250 Mb"). Read as binary
    /// — XFileSharing's limits are 1024-based, and 5250 MB is the kind of odd figure that comes from a
    /// per-host config rather than a marketing round number.
    /// </summary>
    private const long AnonymousMaxFileSizeBytes = 5250L * 1024 * 1024;

    /// <summary>Guest cap; the account path keeps the family default (no account has been tested here,
    /// so nothing stronger is claimed for it).</summary>
    public override long? MaxFileSizeFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? AnonymousMaxFileSizeBytes : base.MaxFileSizeFor(credentials);
}
