// <copyright file="UdropPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// udrop (udrop.com) — <see cref="YetiSharePipeline"/> with a <b>guest</b> upload, verified 2026-08-07
/// by uploading real bytes: <c>GET /assets/js/uploader.js</c> hands a signed-out visitor a complete
/// ticket AND a <b>5 GiB</b> cap, and the node returns
/// <c>[{"error":null,"url":"https://www.udrop.com/OY37/&lt;name&gt;","delete_url":…}]</c>.
/// <para>
/// <b>That cap is the whole difference from Filestank</b>, which serves the same platform, the same
/// script and the same ticket to a guest — with <c>uploaderMaxSize</c> of <b>0</b>, i.e. "this session
/// may not upload". Reading the number rather than trusting a host's marketing is what separates the
/// two modes; see <see cref="YetiSharePipeline"/>.
/// </para>
/// <para>
/// <b>⚠ Its node is the SITE ITSELF</b> (<c>www.udrop.com/ajax/file_upload_handler</c>), not a
/// separate <c>fsNN.</c> storage box, so the upload is an ordinary site route behind the session
/// middleware: without the cookie that <c>uploader.js</c> issued, it answers a <b>404 page</b>.
/// Measured. The base sends the cookie exactly when the node's host is the site's own.
/// </para>
/// <para>
/// Storage is permanent — no expiry, unlike most of the anonymous hosts here.
/// </para>
/// </summary>
public sealed class UdropPipeline : YetiSharePipeline
{
    public UdropPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — drives the uploader.js scrape and the upload from canned responses.</summary>
    internal UdropPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Udrop";

    /// <summary>Downloads are captcha-free: its FAQ answers the question directly: downloaders
    /// never wait on a timer or solve captchas (udrop.com/faq, 2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    protected override string SiteBase => "https://www.udrop.com";

    /// <summary>Verified by uploading a file as a signed-out visitor, not by the ticket merely being
    /// rendered — Filestank renders one too and refuses the bytes.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>Permanent — "no file expiry" is this host's own selling line, unusual among the
    /// anonymous hosts here. udrop's policy, not YetiShare's: the base stays unspecified.</summary>
    public override FileRetention RetentionFor(FileHosterLoginDto credentials) => FileRetention.Permanent;

    /// <summary>
    /// Its sign-in is a plain <c>username</c>/<c>password</c>/<c>submitme</c> form with <b>no
    /// captcha</b> — checked on the live page and against a capture of a real sign-in — so an account
    /// is entered in the app's own dialog and no browser ever opens.
    /// </summary>
    protected override bool SupportsDirectLogin => true;

    /// <summary>
    /// The cap the uploader script declares — <c>uploaderMaxSize = 5368709120</c>.
    /// <para>
    /// <b>An account does NOT raise it.</b> A capture of a real signed-in upload (2026-08-08) shows
    /// the same 5 GiB figure a guest gets, so the file limit is the host's, not the tier's. What an
    /// account buys here is <b>100 GB of storage</b> and the file manager — the uploads land in it and
    /// can be managed and deleted, which an anonymous upload's one-shot delete URL can't match.
    /// </para>
    /// </summary>
    protected override long UploaderMaxSize => 5_368_709_120;

    /// <summary>
    /// It runs an extension blocklist, enforced by the node: <c>.bin</c> comes back
    /// <c>"File could not be uploaded due to that file type being banned by the site admin"</c>.
    /// The list itself is not published, so this only rejects what has actually been measured —
    /// guessing wider would block files the host would have taken.
    /// <para>
    /// The archive set this app moves is fine: <c>.rar</c>, <c>.r00</c>, <c>.sfv</c> and <c>.nfo</c>
    /// were each accepted as a guest.
    /// </para>
    /// </summary>
    public string? RejectedFileExtensionReason(string fileName)
        => Path.GetExtension(fileName).Equals(".bin", StringComparison.OrdinalIgnoreCase)
            ? $"{Name} refuses .bin files (its admin blocks that type)."
            : null;
}
