// <copyright file="FilestankPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Filestank (filestank.com) — the first <see cref="YetiSharePipeline"/> host, from a browser capture
/// of a signed-in upload 2026-08-01. Everything about the protocol lives on the base; what is left
/// here is the site and its two figures.
/// <para>
/// <b>Account-only, and that is measured rather than assumed:</b> a signed-out visitor gets a
/// complete upload ticket whose <c>uploaderMaxSize</c> is <b>0</b> — the platform's way of saying
/// this session may not upload. Its guest-capable siblings (udrop, BowFile) declare a real cap to
/// the same request, which is why they set <see cref="SupportsAnonymousUpload"/> and this doesn't.
/// </para>
/// <para>
/// ⚠ <b>Free accounts get roughly 10 uploads a day</b>, so this is a weak target for a large batch —
/// the base stops the rest of a package cleanly once the node reports the allowance is spent.
/// </para>
/// <para>
/// Its published <c>/api/v2</c> wants two 64-character keys that no page in the account area ever
/// prints, which is why the shipped credential is the session cookie instead.
/// </para>
/// </summary>
public sealed class FilestankPipeline : YetiSharePipeline
{
    public FilestankPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — drives the uploader.js scrape and the upload from canned responses.</summary>
    internal FilestankPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Filestank";

    protected override string SiteBase => "https://www.filestank.com";

    /// <summary>The uploader's own <c>maxFileSize</c> for a signed-in session: 20 GiB.</summary>
    protected override long UploaderMaxSize => 21_474_836_480;
}
