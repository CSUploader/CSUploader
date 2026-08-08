// <copyright file="UploadHivePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.RegularExpressions;
using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// UploadHive (uploadhive.com) — classic XFileSharing with a LIVE anonymous upload, verified
/// 2026-08-08 with real bytes: the family's anonymous field set answers
/// <c>[{"file_code":"…","file_status":"OK"}]</c> and <c>uploadhive.com/&lt;code&gt;</c> serves a
/// download page naming the file.
/// <para>
/// <b>⚠ It nearly slipped through a sweep that should have found it.</b> An earlier pass asked every
/// candidate for <c>?op=api_get_limits</c> — UploadHive answers that with its homepage, so it looked
/// like "not XFileSharing". It is: the family's form is on <c>/upload</c>, not the landing page.
/// <b>A host that doesn't expose one XFS route is not thereby a different platform.</b>
/// </para>
/// <para>
/// <b>⚠ The anonymous FILE form carries no <c>action</c>.</b> The only <c>upload.cgi</c> action on
/// that page belongs to the <i>remote-URL</i> form (<c>upload_type=url</c>), so the base's scrape
/// would have posted files at the URL-import endpoint — a wrong destination that answers plausibly.
/// <see cref="DiscoverAnonymousServerAsync"/> therefore takes that action for its rotating
/// <c>fsNNN.</c> host and rewrites the query to the file upload the form actually performs.
/// </para>
/// <para>
/// <b>No per-file cap</b>: its uploader config declares <c>max_upload_filesize: '0'</c>, which on this
/// fork means unlimited (a 0 that meant "ten bytes" is what DropGalaxy had — the difference is that
/// uploads here actually succeed). The base's 1 GiB default would silently skip every larger file, so
/// it is overridden rather than inherited.
/// </para>
/// </summary>
public sealed class UploadHivePipeline : XFileSharingApiPipeline
{
    /// <summary>The anonymous form lives here, not on the homepage.</summary>
    private const string UploadPagePath = "/upload";

    /// <summary>
    /// Extensions the host refuses, taken from its own uploader config
    /// (<c>ext_not_allowed: '7z|001'</c>) and confirmed by uploading one of each: both come back
    /// <c>{"file_code":"undef","file_status":"unallowed extension"}</c> — <b>after</b> the whole file
    /// has transferred, which is exactly what this hook exists to prevent.
    /// <para>
    /// ⚠ <c>.001</c> is the first volume of a split archive, so a package can be refused at its most
    /// important part while every other volume uploads happily.
    /// </para>
    /// </summary>
    private static readonly string[] BlockedExtensions = [".7z", ".001"];

    public UploadHivePipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow.</summary>
    internal UploadHivePipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "UploadHive";

    protected override string Host => "https://uploadhive.com";

    /// <summary>Verified by uploading real bytes and fetching the resulting page — not by the form
    /// rendering, which DropGalaxy, Uploady and Clicknupload all did while refusing the bytes.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>See the class remarks: the host declares no ceiling, and the base's 1 GiB default
    /// would reject larger files before they were ever offered.</summary>
    public override long? MaxFileSize => null;

    /// <inheritdoc/>
    public override string? RejectedFileExtensionReason(string fileName)
        => BlockedExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase)
            ? $"{Name} doesn't accept {Path.GetExtension(fileName).ToLowerInvariant()} files."
            : null;

    /// <summary>
    /// Resolves the anonymous upload node. The page's only <c>upload.cgi</c> action is the
    /// remote-URL form's, so its rotating <c>fsNNN.</c> host is reused and the query rewritten to the
    /// file upload — which is what the file form's own JavaScript does.
    /// </summary>
    protected override async Task<(string? UploadUrl, string? Error)> DiscoverAnonymousServerAsync(AttemptContext ctx, CancellationToken ct)
    {
        (string? scraped, string? error) = await base.DiscoverAnonymousServerAsync(ctx, ct).ConfigureAwait(false);
        if (scraped is null)
        {
            return (null, error);
        }

        int query = scraped.IndexOf('?', StringComparison.Ordinal);
        string script = query < 0 ? scraped : scraped[..query];
        return ($"{script}?upload_type=file&utype=anon", null);
    }

    /// <summary>The anonymous form is on <c>/upload</c>; the homepage carries none. Keeps the base's
    /// cache-buster so a retry can be handed a freshly-assigned node.</summary>
    protected override string BuildAnonUploadFormUrl(string cacheBuster) => $"{Host}{UploadPagePath}?_={cacheBuster}";
}
