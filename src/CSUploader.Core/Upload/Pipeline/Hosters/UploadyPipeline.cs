// <copyright file="UploadyPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Uploady — classic XFileSharing; the protocol lives in <see cref="XFileSharingApiPipeline"/>.
/// <para>
/// Probed live 2026-07-26. Uploady differs from its siblings in one way that matters: its HOMEPAGE
/// carries no upload form at all (only links to the upload page), so the anonymous form is scraped
/// from <c>?op=upload_form</c> instead — see <see cref="BuildAnonUploadFormUrl"/>. That page renders
/// the standard anonymous form (<c>&lt;form id="uploadfile"
/// action="https://lswN.gamezizo.com/cgi-bin/upload.cgi?upload_type=file&amp;utype=anon"&gt;</c>,
/// empty <c>sess_id</c>) FIRST, ahead of the separate remote/URL-upload form that posts to the same
/// <c>upload.cgi</c> path without a query — so the base's first-match scrape picks the right one.
/// Upload nodes live on a separate domain (<c>gamezizo.com</c>); the POST follows the scraped action.
/// It is genuinely stock XFS: <c>?op=api_get_limits</c> answers with the standard XML.
/// </para>
/// </summary>
public sealed class UploadyPipeline : XFileSharingApiPipeline
{
    public UploadyPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow
    /// from canned responses.</summary>
    internal UploadyPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Uploady";

    protected override string Host => "https://uploady.io";

    /// <summary>Anonymous (not-logged-in) upload verified against the live <c>?op=upload_form</c> page.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>
    /// 5 GB, read from the host itself: <c>?op=api_get_limits</c> reports
    /// <c>&lt;MaxUploadFilesize&gt;5120&lt;/MaxUploadFilesize&gt;</c> (MB) for an anonymous session.
    /// </summary>
    public override long? MaxFileSize => 5120L * 1024 * 1024;

    /// <summary>Uploady renders the anonymous form only on the upload page — its homepage (the family's
    /// usual spot, and the base's default) has no form at all.</summary>
    protected override string BuildAnonUploadFormUrl(string cacheBuster)
        => $"{Host}/?op=upload_form&_={cacheBuster}";
}
