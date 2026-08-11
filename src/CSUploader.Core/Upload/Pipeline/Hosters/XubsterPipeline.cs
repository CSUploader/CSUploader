// <copyright file="XubsterPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Text.RegularExpressions;
using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Xubster (xubster.com) — classic XFileSharing, <b>anonymous at 10 MB</b> or <b>500 MB</b> signed in
/// (1 GB account storage).
/// <para>
/// ⚠ <b>It was on the "XFS family, sweep concluded no anonymous" list, and that was wrong</b> — the
/// same way it was wrong about UpZur, BtaFile and World Files. That sweep looked for a static
/// <c>utype=anon</c> form; this host renders none (signed out, <c>?op=upload</c> 302s to the login),
/// and the node its own keyless <c>?op=api_get_limits</c> names accepts the family's guest field set
/// regardless. <b>Fourth host on that theme.</b> The guest cap is small enough to be nearly
/// decorative, but it is real, published by the host, and enforced here.
/// </para>
/// <para>
/// Three things to know about its nodes. They are on a <b>different registered domain</b>
/// (<c>xubster.ink</c>, not <c>.com</c>), they <b>rotate</b> (x13, x21 and x100 in the space of an
/// hour), and they are <b>not consistently on 443</b> — the limits call has answered both
/// <c>https://x21.xubster.ink/cgi-bin</c> and <c>https://x100.xubster.ink:8443/cgi-bin</c>. Nothing
/// here may assume any part of that, which is why the node is asked for per upload and used verbatim.
/// </para>
/// <para>
/// ⚠ <b>Its upload page is <c>?op=upload</c>, not the family's <c>?op=upload_form</c></b> — the
/// BtaFile quirk. The family route exists and answers 200, but renders the homepage with no form on
/// it, so pointing the base at it reports a perfectly good session as expired.
/// </para>
/// <para>
/// ⚠ <b>It publishes an extension blocklist</b> (<c>ExtNotAllowed</c>): exe, php, php.jpeg, php.jpg,
/// sh, apk. Checked locally before any bytes move, as on <see cref="UploadrarPipeline"/>.
/// </para>
/// <para>
/// Everything else is stock: <c>/login.html</c> exists, the login has no enforced captcha (the form
/// carries a <c>g-recaptcha-response</c> field, and a sign-in with it absent succeeds — only
/// REGISTRATION is gated, by hCaptcha, and that is not a route this app takes), and Cloudflare fronts
/// the site without challenging this client.
/// </para>
/// </summary>
public sealed class XubsterPipeline : XFileSharingApiPipeline
{
    /// <summary>The keyless limits call, which is also where the anonymous node comes from.</summary>
    private const string ApiGetLimitsPath = "/?op=api_get_limits";

    /// <summary>Guest cap — <c>&lt;MaxUploadFilesize&gt;10&lt;/MaxUploadFilesize&gt;</c> from the
    /// signed-out limits call. Binary, as XFileSharing's limits are 1024-based.</summary>
    private const long AnonymousMaxFileSizeBytes = 10L * 1024 * 1024;

    /// <summary>Account cap — the signed-in upload page's own <c>max_upload_filesize: '500'</c>.</summary>
    private const long AccountMaxFileSizeBytes = 500L * 1024 * 1024;

    /// <summary>
    /// Verbatim from <c>?op=api_get_limits</c> → <c>ExtNotAllowed</c> (read live 2026-08-11):
    /// <c>EXE Files|*.exe|PHP Files|*.php|PHP.JPEG Files|*.php.jpeg|PHP.JPG Files|*.php.jpg|SH
    /// Files|*.sh|APK Files|*.apk</c>.
    /// <para>
    /// A snapshot rather than a per-upload fetch, for the reason Uploadrar's is: one more request on
    /// every file for a list that changes rarely, and a stale entry fails no worse than today.
    /// </para>
    /// </summary>
    private static readonly string[] BlockedExtensions = ["exe", "php", "php.jpeg", "php.jpg", "sh", "apk"];

    /// <summary>&lt;ServerURL&gt;https://x100.xubster.ink:8443/cgi-bin&lt;/ServerURL&gt; — the cgi-bin
    /// DIRECTORY (host, port and number all vary), so the script name is appended and nothing else is
    /// assumed about it.</summary>
    private static readonly Regex ServerUrlRegex = new(
        """<ServerURL>\s*([^<\s]+)\s*</ServerURL>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public XubsterPipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow from
    /// canned responses.</summary>
    internal XubsterPipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? postFormOverride = null)
        : base(authService, loginRepository, getOverride, uploadOverride, postFormOverride)
    {
    }

    public override string Name => "Xubster";

    protected override string Host => "https://xubster.com";

    /// <summary>Verified by uploading real bytes as a guest and fetching the page that came back —
    /// not by a form being rendered, because there is no guest form to render.</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>No REST API to prefer: <c>/api/upload/server</c> answers a 70-byte non-answer, so the
    /// account uploads through the logged-in web form.</summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>The login page carries no enforced captcha and posting the family's form from this
    /// app's own stack answers <c>302 + Set-Cookie: xfss</c> — checked live, with the form's
    /// <c>g-recaptcha-response</c> field omitted entirely. So no sign-in window opens.</summary>
    protected override bool SupportsDirectLogin => true;

    /// <summary>
    /// <b><c>?op=upload</c>, not the family's <c>?op=upload_form</c>.</b> The family route exists and
    /// answers 200, but what it renders is the homepage — no form, no <c>sess_id</c> — so the base
    /// would report a good session as expired. Same quirk as BtaFile, opposite spelling.
    /// </summary>
    protected override string UploadFormUrl => Host + "/?op=upload";

    /// <summary>The family's own account page, which here carries both the name and the storage row
    /// (its theme has no <c>fa-user</c> icon and states usage as "0.00 of 1 GB" — both patterns live
    /// on the base, shared with World Files).</summary>
    protected override string WebFormAccountPageUrl => MyAccountUrl;

    /// <summary>10 MB as a guest, 500 MB signed in — each figure stated by the host on its own side
    /// of the sign-in.</summary>
    public override long? MaxFileSizeFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? AnonymousMaxFileSizeBytes : AccountMaxFileSizeBytes;

    /// <summary>
    /// Refuses a blocked type before any bytes move. XFileSharing enforces this list at the END of an
    /// upload, so without the check the user pays for the whole transfer to be told no.
    /// </summary>
    public override string? RejectedFileExtensionReason(string fileName)
    {
        string name = Path.GetFileName(fileName);
        foreach (string blocked in BlockedExtensions)
        {
            if (name.EndsWith('.' + blocked, StringComparison.OrdinalIgnoreCase))
            {
                return $"Xubster doesn't accept .{blocked} files "
                       + $"(it blocks {string.Join(", ", BlockedExtensions.Select(e => "." + e))}). "
                       + "Archive the file first — .rar/.zip parts upload normally.";
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the node out of <c>?op=api_get_limits</c> rather than off a form, because signed out this
    /// host renders none — <c>?op=upload</c> 302s to the login. The <c>&lt;ServerURL&gt;</c> is used
    /// verbatim: its host, its number and its PORT all vary between calls.
    /// </summary>
    protected override async Task<(string? UploadUrl, string? Error)> DiscoverAnonymousServerAsync(AttemptContext ctx, CancellationToken ct)
    {
        string xml;
        try
        {
            xml = await GetAsync(ctx, Host + ApiGetLimitsPath, NoCacheHeaders, ct);
        }
        catch (Exception ex)
        {
            return (null, $"{Name}: upload-server lookup failed: {ex.Message}");
        }

        Match m = ServerUrlRegex.Match(xml);
        if (!m.Success)
        {
            return (null, $"{Name}: ?op=api_get_limits carried no <ServerURL>: {Snippet(xml)}");
        }

        string node = m.Groups[1].Value.TrimEnd('/');
        return ($"{node}/upload.cgi?upload_type=file&utype=anon", null);
    }

    /// <summary>Test seams: the two routes this fork moves, and the account-page scrapes it leans on
    /// the base for.</summary>
    internal (string Upload, string Account) RoutesForTests => (UploadFormUrl, WebFormAccountPageUrl);

    /// <inheritdoc cref="RoutesForTests"/>
    internal string? ParseAccountUsernameForTests(string html) => ParseAccountUsername(html);

    /// <inheritdoc cref="RoutesForTests"/>
    internal (long? Used, long? Quota) ParseStorageUsageForTests(string html) => ParseStorageUsage(html);
}
