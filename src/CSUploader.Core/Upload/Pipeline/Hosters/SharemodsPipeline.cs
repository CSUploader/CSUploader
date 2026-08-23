// <copyright file="SharemodsPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// ShareMods (sharemods.com) — <b>DISABLED 2026-08-02, the day it was written</b>. Not because the
/// upload failed: it is genuinely anonymous and two guest uploads were verified with real bytes. It
/// is off because Cloudflare then began challenging this client and the reason was never settled —
/// see the warning below, which is the whole story and the re-enable condition. The four wire-up
/// touchpoints are commented out; this class and its tests are complete and stay in the tree.
/// <para>
/// Were it enabled, it would be a rarity: by 2026-08-02 the family has all but moved to
/// account-required uploads (filedot, TeraBytez, kenfiles, fastfile, DataVaults and Clicknupload all
/// refuse guests outright).
/// </para>
/// <para>
/// <b>Verified end to end, anonymously, before a line of this was written:</b> its homepage renders
/// the classic guest form (<c>utype=anon</c> beside an empty <c>sess_id</c>), a multipart post to the
/// node in that page answered <c>[{"file_code":"…","file_status":"OK"}]</c>, and both resulting
/// <c>sharemods.com/&lt;code&gt;</c> pages served a download page naming the uploaded file. The
/// chunk-accepting hosts that later refuse at finalise taught this: only the stored file counts.
/// </para>
/// <para>
/// Two host-specific details, both discovered the hard way:
/// <list type="bullet">
///   <item><b>The page's only <c>upload.cgi</c> action belongs to the URL-uploader</b>
///   (<c>?upload_type=url</c>), because the file form's action is set by script. The family scrape
///   therefore finds a real-looking endpoint that imports links rather than storing bytes — the same
///   trap filedot.to sets. <see cref="DiscoverAnonymousServerAsync"/> rewrites it.</item>
///   <item><b>It answers bursts from one address with a Cloudflare challenge</b> — see the warning
///   below. Uploads are capped at two at once because of it; see
///   <see cref="MaxConcurrentUploadsFor"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>⚠ UNRESOLVED: Cloudflare challenged this client after heavy probing, and had not relented three
/// minutes later.</b> The sequence, honestly: the mechanism was verified with real bytes (two guest
/// uploads through a plain .NET client, both files served), then perhaps fifty probe requests later
/// the apex began answering every .NET request with a managed "Just a moment…" 403 — every header
/// shape, with and without a User-Agent, HTTP/1.1 and /2 alike — while Python's OpenSSL-backed client
/// kept getting 200s from the same machine in the same minute. Header shape is therefore not the
/// variable; the TLS fingerprint is, which is the wall
/// <c>TakeFilePipeline</c> documents and which no header or cookie can climb.
/// </para>
/// <para>
/// What cannot be told apart from here is a PERMANENT block on this client from an IP+client
/// reputation penalty EARNED by that probing. The same .NET stack succeeded here before the volume,
/// so both readings survive the evidence. <b>Re-enable condition: one upload completing from an
/// address that has not been probing.</b> Nothing else needs building — the pipeline is finished and
/// tested. Do NOT spend time on cf_clearance forwarding, which was already built for TakeFile and
/// proven not to beat a managed challenge.
/// </para>
/// <para>
/// A mods-oriented host by branding, general-purpose by mechanism. 200 MB per file, no account, no
/// captcha.
/// </para>
/// </summary>
public sealed class SharemodsPipeline : XFileSharingApiPipeline
{
    public SharemodsPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — drives the form-page GET and the multipart upload from canned responses.</summary>
    internal SharemodsPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, SpeedBudget?, Task<HttpResponseSnapshot>> uploadOverride)
        : base(authService, loginRepository, getOverride, uploadOverride)
    {
    }

    public override string Name => "ShareMods";

    protected override string Host => "https://sharemods.com";

    /// <summary>The whole point of this host — see the class remarks for the live verification.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>
    /// 200 MB, the figure its own uploader prints ("Maximum file size 200 Mb"). Read as binary, as
    /// everywhere in this family. There is no larger tier to miss: guests and accounts alike are
    /// offered this one number.
    /// </summary>
    public override long? MaxFileSize => 200L * 1024 * 1024;

    /// <summary>
    /// <b>Two.</b> This host escalates against volume from one address — a few dozen page fetches
    /// were enough to earn a Cloudflare challenge that outlasted a three-minute cooldown (see the
    /// class remarks).
    /// <para>
    /// Unlike DataVaults, whose limit was pinned to an exact number, this one was never measured
    /// cleanly: every attempt to bracket it tripped the escalation and poisoned the next reading. So
    /// this is deliberately conservative rather than precise — an upload that waits costs a user
    /// nothing, while being challenged mid-batch costs them the file AND, apparently, the next while.
    /// Raise it only against a clean measurement.
    /// </para>
    /// </summary>
    public override int? MaxConcurrentUploadsFor(FileHosterLoginDto credentials) => 2;

    /// <summary>
    /// Rewrites the scraped action from the URL-importer to the file uploader.
    /// <para>
    /// The base scrapes the first <c>action</c> containing <c>upload.cgi</c>, which is correct on
    /// every host whose file form carries one. Here it doesn't: the page ships
    /// <c>…/upload.cgi?upload_type=url</c> for the "upload from a link" box and lets script fill in
    /// the file form's action. Posting a file to the URL importer is exactly the kind of failure that
    /// looks like success — a real endpoint, a plausible reply, and nothing stored.
    /// </para>
    /// </summary>
    protected override async Task<(string? UploadUrl, string? Error)> DiscoverAnonymousServerAsync(AttemptContext ctx, CancellationToken ct)
    {
        (string? url, string? error) = await base.DiscoverAnonymousServerAsync(ctx, ct).ConfigureAwait(false);
        return url is null ? (null, error) : (ToFileUploadUrl(url), null);
    }

    /// <summary>Turns <c>…upload.cgi?upload_type=url</c> into the file-upload form of the same
    /// endpoint. Internal for testing.</summary>
    internal static string ToFileUploadUrl(string action)
        => action.Replace("upload_type=url", "upload_type=file", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The field set proven against this host, which is the page's own rather than the family default
    /// hexload set the base ships: it carries <c>file_descr</c> and <c>file_public=1</c>, and no
    /// <c>mode</c>. Both of the verification uploads used exactly this.
    /// <para>
    /// Whether hexload's set would also be accepted here is UNKNOWN — the host's rate limiter blocked
    /// every attempt to test it. Since this parser is field-presence sensitive, the proven set ships.
    /// </para>
    /// </summary>
    protected override Dictionary<string, string> BuildAnonymousExtraFields() => new(StringComparer.Ordinal)
    {
        ["sess_id"] = string.Empty,
        ["utype"] = "anon",
        ["file_descr"] = string.Empty,
        ["file_public"] = "1",
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
        ["upload"] = "Start upload",
        ["keepalive"] = "1",
    };
}
