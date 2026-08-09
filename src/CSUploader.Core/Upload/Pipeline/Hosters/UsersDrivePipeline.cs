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
    /// It has a working REST API — <c>/api/upload/server</c> and <c>/api/account/info</c> both answer
    /// the family's <c>{"msg":"Invalid key"}</c> — but <b>no page ever prints a key</b>: the account
    /// area's nav is check_files / my_files / my_reports / payments / make_money / my_referrals /
    /// news / upload_form, and none of them mentions one. So the API is unusable as a first-run
    /// credential and the account ships on the web-form path, exactly as DDownload did.
    /// </summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>
    /// Its <b>login</b> page is a plain <c>login</c>/<c>password</c> form with no captcha (checked
    /// live), so an account is typed into the app's own dialog and no browser window opens.
    /// <para>
    /// ⚠ Not to be confused with its <b>registration</b> page, which a capture shows posting a
    /// <c>g-recaptcha-response</c>. This app never registers, so that captcha is irrelevant — but a
    /// glance at the wrong form would wrongly condemn the host to the WebView path.
    /// </para>
    /// </summary>
    protected override bool SupportsDirectLogin => true;

    /// <summary>
    /// 5250 MB, the figure the upload page states itself ("Max file size is 5250 Mb"). Read as binary
    /// — XFileSharing's limits are 1024-based, and 5250 MB is the kind of odd figure that comes from a
    /// per-host config rather than a marketing round number.
    /// </summary>
    private const long AnonymousMaxFileSizeBytes = 5250L * 1024 * 1024;

    /// <summary>
    /// <b>10500 MB — an account doubles the guest cap</b>, and this is the host's own wording on the
    /// signed-in <c>?op=upload_form</c> page ("Max file size is 10500 Mb"), from a capture of a real
    /// registered session.
    /// <para>
    /// ⚠ <b>The account page's prominent "75000 Mb" is NOT this.</b> It sits under "Traffic available
    /// today" — a daily bandwidth allowance, not a per-file limit. Clicknupload set the identical trap
    /// (its storage figure's neighbour is bandwidth), so read the label, not the nearest number.
    /// </para>
    /// </summary>
    private const long RegisteredMaxFileSizeBytes = 10500L * 1024 * 1024;

    /// <summary>Guest 5250 MB, registered 10500 MB — both quoted by the host on the upload form it
    /// serves to that session.</summary>
    public override long? MaxFileSizeFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? AnonymousMaxFileSizeBytes : RegisteredMaxFileSizeBytes;

    /// <summary>Test seams — both flags decide which auth path this host takes, and neither is
    /// observable from outside the family otherwise.</summary>
    internal bool UsesWebFormUploadForTests => UsesWebFormUpload;

    internal bool SupportsDirectLoginForTests => SupportsDirectLogin;
}
