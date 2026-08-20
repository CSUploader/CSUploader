// <copyright file="UploadrarPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Uploadrar (uploadrar.com) — account upload on the standard XFileSharing REST API, so the
/// protocol itself lives in <see cref="XFileSharingApiPipeline"/> and this supplies little more than
/// a name and a host. Its <c>/api/account/info</c> and <c>/api/upload/server</c> answer the family's
/// usual shapes (verified live 2026-08-02: a bogus key returns
/// <c>{"status":400,"msg":"Invalid key"}</c> from both).
/// <para>
/// <b>It refuses common media extensions, and only says so at the very end.</b>
/// <c>?op=api_get_limits</c> publishes
/// <c>ExtNotAllowed: MP4|MPG|WMV|MKV|M4V|AVI|MP3</c> — but the upload node happily accepts the bytes
/// and the FINALISE step is what rejects them. A capture of a real upload shows a 5 MB
/// <c>.avi</c> transferring in full, <c>put_chunk.cgi</c> answering <c>{"status":"OK"}</c>, and then
/// <c>import_file</c> replying <c>{"error":"unallowed extension"}</c>. So the whole transfer is spent
/// to earn a refusal. <see cref="PreflightRejection"/> checks the extension locally first; the same
/// capture's <c>.srr</c> upload succeeded, which is the shape this app actually posts.
/// </para>
/// <para>
/// <b>Anonymous is disabled</b>, and its <c>api_get_limits</c> says so in the DropGalaxy dialect:
/// <c>MaxUploadFilesize 0.00001</c> (≈10 bytes) for a signed-out caller. That figure describes the
/// anonymous tier only — a registered account uploads normally, which is why this ships as
/// account-only rather than disabled.
/// </para>
/// <para>
/// Its web UI uses the <b>xfspro</b> chunked plugin (<c>op=start_upload</c> →
/// <c>put_chunk.cgi</c> + <c>X-Upload-SID</c> → <c>api.cgi op=import_file</c>) — the same variant
/// <see cref="FilehosterIoPipeline"/> implements, and the second host seen on it. That path is NOT
/// used here: the REST API this family exposes is the simpler route and needs no bespoke pipeline.
/// If the API upload ever proves unusable on this host, the xfspro flow is fully captured and
/// FilehosterIo's implementation is the model — at which point extracting a shared xfspro base
/// (rather than a third copy) is the right move.
/// </para>
/// </summary>
public sealed class UploadrarPipeline : XFileSharingApiPipeline
{
    /// <summary>
    /// Verbatim from <c>?op=api_get_limits</c> → <c>ExtNotAllowed</c> (read live 2026-08-02):
    /// <c>MP4 Files|*.mp4|MPG Files|*.mpg|WMV Files|*.wmv|MKV Files|*.mkv|M4V Files|*.m4v|AVI
    /// Files|*.avi|MP3 Files|*.mp3</c>.
    /// <para>
    /// Held as a snapshot rather than fetched per upload: it is one more request on every file for a
    /// list that changes rarely, and a stale entry fails no worse than today (the server still
    /// refuses, just later). Re-read that endpoint if the refusals stop matching.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mpg", "wmv", "mkv", "m4v", "avi", "mp3",
    };

    public UploadrarPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — same shape as the other XFS shims'.</summary>
    internal UploadrarPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "Uploadrar";

    /// <summary>Downloads are captcha-free: its premium row "No downloads captcha" is green for
    /// free tiers and the live free flow ends in a direct link (2026-08-20).</summary>
    public override DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    /// <summary>From its own premium.html (read 2026-08-12), "When are your files deleted?":
    /// anonymous 1, registered 90, premium 120, premium pro 360 - all "days after last
    /// download".</summary>
    public override FileRetention RetentionFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? FileRetention.DaysAfterLastDownload(1)
            : credentials.AccountType switch
            {
                AccountType.Premium => FileRetention.DaysAfterLastDownload(120),
                AccountType.Pro => FileRetention.DaysAfterLastDownload(360),
                _ => FileRetention.DaysAfterLastDownload(90),
            };

    protected override string Host => "https://uploadrar.com";

    /// <summary>
    /// No declared cap — the server decides. NOT the base's 1 GiB default, which would silently skip
    /// every file above it at queue time on a host whose registered tier is advertised at 6 GB. That
    /// 6 GB is itself unverified, and encoding a guess rejects files the server would have taken;
    /// the only figure this host publishes is <c>?op=api_get_limits</c>'s <c>MaxUploadFilesize
    /// 0.00001</c>, which describes the ANONYMOUS tier we don't use.
    /// </summary>
    public override long? MaxFileSize => null;

    /// <summary>
    /// <c>/login/</c>, not the family's <c>/login.html</c> — which <b>404s</b> here (checked live
    /// 2026-08-02). Without this the sign-in WebView opens a not-found page and no account can ever
    /// be added.
    /// <para>
    /// The site is inconsistent about it: its own signed-out redirects still point at
    /// <c>/login.html</c> (<c>/?op=my_account</c> and <c>/files/</c> both 302 there), so following one
    /// lands on a 404. That is their bug, not ours — we navigate to the page that works.
    /// </para>
    /// </summary>
    protected override string LoginPagePath => "/login/";

    /// <summary>
    /// This fork's templates use trailing-slash routes rather than the family's <c>?op=</c> ones — the
    /// live login page links <c>/login/</c> and never <c>?op=login</c>, and a captured session logged
    /// out via <c>/logout/</c>. The base's default looks for <c>op=logout</c> and would therefore call
    /// a perfectly good session signed-out, exactly as it did for DDownload. Both forms are accepted
    /// because the server honours both, so which one a given page happens to emit is not worth
    /// betting the sign-in on.
    /// </summary>
    protected override bool LooksSignedIn(string html)
        => html.Contains("op=logout", StringComparison.OrdinalIgnoreCase)
           || html.Contains("/logout/", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when Uploadrar's published blocklist covers this extension. Internal for testing.</summary>
    internal static bool IsBlockedExtension(string fileName)
        => BlockedExtensions.Contains(Path.GetExtension(fileName).TrimStart('.'));

    /// <summary>
    /// Refuses a blocked extension before the upload starts — see the class remarks for why that
    /// matters here specifically (the host takes the whole file first, then rejects it). The base's
    /// PreflightRejection defaults to this, so the upload path needs no separate override.
    /// <para>
    /// It is also what the UPLOAD WIZARD calls, so these files are dropped from Uploadrar's
    /// column and names them in the warning panel <b>before the user presses Next</b> — rather than
    /// each one failing individually at upload time. One rule, two consumers.
    /// </para>
    /// </summary>
    public override string? RejectedFileExtensionReason(string fileName)
        => IsBlockedExtension(fileName)
            ? $"Uploadrar doesn't accept {Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant()} files "
                + $"(it blocks {string.Join(", ", BlockedExtensions.Order(StringComparer.OrdinalIgnoreCase)).ToUpperInvariant()}). "
                + "Archive the file first — .rar/.zip/.srr parts upload normally."
            : null;
}
