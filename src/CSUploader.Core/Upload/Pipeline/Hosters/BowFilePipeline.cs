// <copyright file="BowFilePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// BowFile (bowfile.com) — <see cref="YetiSharePipeline"/> with a <b>guest</b> upload, verified
/// 2026-08-07 by uploading real bytes: a signed-out <c>GET /assets/js/uploader.js</c> hands back a
/// ticket and a <b>20 GiB</b> cap, and the node answers
/// <c>[{"error":null,"url":"https://bowfile.com/1tvbv",…}]</c>.
/// <para>
/// <b>Its node is a separate storage host</b> (<c>fsNN.bowfile.com</c>, rotating), which is the other
/// half of the pattern udrop shows: the site's session cookie is host-only and never reaches it, and
/// it doesn't need one — the node authenticates on the <c>_sessionid</c> FIELD. Confirmed by
/// uploading with no cookie at all. The base decides this per host by comparing the node's host with
/// the site's, so neither shape needs a flag.
/// </para>
/// <para>
/// The share link comes back on the APEX rather than the node, so it is used exactly as returned.
/// </para>
/// </summary>
public sealed class BowFilePipeline : YetiSharePipeline
{
    public BowFilePipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — drives the uploader.js scrape and the upload from canned responses.</summary>
    internal BowFilePipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "BowFile";

    /// <summary>From its own FAQ (read 2026-08-12): "Free/non accounts files are kept for 20 days.
    /// Premium accounts files are kept for 100 days." - "non accounts" covers guest uploads. The FAQ
    /// does not say what the days count from, so this reports the from-upload floor.</summary>
    public override FileRetention RetentionFor(FileHosterLoginDto credentials)
        => !credentials.IsAnonymous && credentials.AccountType == AccountType.Premium
            ? FileRetention.DaysAfterUpload(100)
            : FileRetention.DaysAfterUpload(20);

    protected override string SiteBase => "https://bowfile.com";

    /// <summary>Verified by uploading a file as a signed-out visitor.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>
    /// Its sign-in is a plain <c>username</c>/<c>password</c>/<c>submitme</c> form with <b>no
    /// captcha</b> — checked on the live page and against a capture of a real sign-in — so an account
    /// is entered in the app's own dialog and no browser ever opens.
    /// </summary>
    protected override bool SupportsDirectLogin => true;

    /// <summary>
    /// The cap its uploader script declares — <c>uploaderMaxSize = 21474836480</c>, four times
    /// udrop's and the same figure Filestank gives a signed-in account.
    /// <para>
    /// <b>An account does NOT raise it</b>: a capture of a real signed-in upload (2026-08-08) shows
    /// the same 20 GiB a guest gets. An account files the uploads under it rather than buying a
    /// bigger file.
    /// </para>
    /// </summary>
    protected override long UploaderMaxSize => 21_474_836_480;
}
