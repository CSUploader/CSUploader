// <copyright file="BtaFilePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.RegularExpressions;
using CSUploader.Dal;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// BtaFile (btafile.com) — stock XFileSharing on the classic web-form upload, <b>anonymous at 100 MB
/// or signed in at 10 GB</b>. Anonymous was verified by uploading real bytes from a cold client with
/// no account: the family's anonymous field set posted to the node answers
/// <c>[{"file_code":"…","file_status":"OK"}]</c> and <c>btafile.com/&lt;code&gt;</c> serves a download
/// page naming the file.
/// <para>
/// <b>It has no REST API</b> — <c>/api/upload/server</c> and <c>/api/account/info</c> both answer the
/// fork's HTML "File Not Found" page, not the family's <c>{"msg":"Invalid key"}</c> — so an account
/// ships on the web-form path. Its sign-in is a plain <c>op=login</c> form with no captcha, posted
/// from this app's own stack, so no browser window opens.
/// </para>
/// <para>
/// <b>Two fork quirks, both of which would fail silently rather than loudly:</b>
/// </para>
/// <list type="number">
///   <item><b>The upload form is on <c>?op=upload</c>, not <c>?op=upload_form</c>.</b> The family's
///   page exists here and returns 200 to a signed-in caller — it simply renders no upload form, so
///   the base's scrape would find nothing and report the session as expired. See
///   <see cref="UploadFormUrl"/>.</item>
///   <item><b>Both of its forms carry <c>?upload_type=<u>url</u></c></b>, the URL-importer's query,
///   including the one the file goes to. That is not a scrape mistake to correct: a browser capture
///   shows a 5 MB file posted to exactly that action and accepted, and this app's own anonymous probe
///   confirmed the node takes a file either way. So the scraped action is used verbatim — the
///   opposite call to filedot.to, where the only action on the page belonged to a URL importer that
///   would NOT have taken the bytes.</item>
/// </list>
/// <para>
/// The homepage renders no anonymous form at all, so the node comes from the host's own keyless
/// <c>?op=api_get_limits</c> — the same route <see cref="UpZurPipeline"/> uses, and the same fork
/// template down to the <c>div.freespace</c> storage bar (which is why that bar now lives on the base).
/// </para>
/// </summary>
public sealed class BtaFilePipeline : XFileSharingApiPipeline
{
    /// <summary>The keyless limits call, which is also where the upload node comes from.</summary>
    private const string ApiGetLimitsPath = "/?op=api_get_limits";

    /// <summary><c>&lt;ServerURL&gt;https://s200.btafile.com/cgi-bin&lt;/ServerURL&gt;</c> — the
    /// cgi-bin DIRECTORY, not the script, so the script name is appended below.</summary>
    private static readonly Regex _serverUrlRegex = new(
        """<ServerURL>\s*([^<\s]+)\s*</ServerURL>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The account name, which this theme prints on <c>?op=my_files</c> only inside the script that
    /// builds the "your public folder" box:
    /// <c>$(input).attr('value', 'https://btafile.com/users/&lt;name&gt;/')</c>.
    /// <para>
    /// The family default anchors on a <c>fa-user</c> account-menu icon this theme doesn't have, so it
    /// returns null and the account saves with a blank name. Anchoring on the <c>/users/</c> path
    /// rather than on nearby chrome is deliberate: that segment can only be an account name, whereas
    /// an icon-adjacent token is whatever the theme put next to the icon — which is how Uploady's
    /// accounts all saved as "Profile".
    /// </para>
    /// <para>
    /// ⚠ What is scraped here REPLACES the typed username on the stored account, and that value is
    /// what the next sign-in posts. Checked against the live host: the name in this link is the login
    /// identifier, character for character (<c>?op=my_account</c> prints the same one in its
    /// "Username" row).
    /// </para>
    /// </summary>
    private static readonly Regex _usernameRegex = new(
        """btafile\.com/users/([A-Za-z0-9._-]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The host's own <c>MaxUploadFilesize</c> (MB) from the keyless limits call made
    /// WITHOUT a session. Binary, as XFileSharing's limits are 1024-based.</summary>
    private const long AnonymousMaxFileSizeBytes = 100L * 1024 * 1024;

    /// <summary>The same call made WITH the account's cookie answers 10240 — an account is a hundred
    /// times the guest cap, and the upload page's own JS agrees (<c>max_upload_filesize: '10240'</c>).</summary>
    private const long RegisteredMaxFileSizeBytes = 10240L * 1024 * 1024;

    public BtaFilePipeline(IInteractiveAuthService? authService = null, FileHosterLoginRepository? loginRepository = null)
        : base(authService, loginRepository)
    {
    }

    /// <summary>Test ctor — delegates to the base test ctor so fixtures can drive the flow from
    /// canned responses.</summary>
    internal BtaFilePipeline(
        IInteractiveAuthService? authService,
        FileHosterLoginRepository? loginRepository,
        Func<string, IReadOnlyDictionary<string, string>?, Task<string>> getOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? postFormOverride = null)
        : base(authService, loginRepository, getOverride, uploadOverride, postFormOverride)
    {
    }

    public override string Name => "BtaFile";

    protected override string Host => "https://btafile.com";

    /// <summary>Verified by uploading real bytes with no account and fetching the resulting page — not
    /// by a form being rendered, which is what DropGalaxy, Uploady and Clicknupload each had while
    /// refusing the bytes. (This host renders no anonymous form at all, and takes the upload anyway.)</summary>
    public override bool SupportsAnonymousUpload => true;

    /// <summary>No REST API here: <c>/api/upload/server</c> and <c>/api/account/info</c> both answer
    /// the fork's HTML "File Not Found" page. An account signs in for the <c>xfss</c> cookie and
    /// uploads through the logged-in form.</summary>
    protected override bool UsesWebFormUpload => true;

    /// <summary>Its <c>/login.html</c> is a plain <c>login</c>/<c>password</c> form with no captcha,
    /// and posting it from this app's own stack answers <c>302 + Set-Cookie: xfss</c> — verified
    /// against a real account. So no sign-in browser opens.</summary>
    protected override bool SupportsDirectLogin => true;

    /// <summary>
    /// <b><c>?op=upload</c>, not the family's <c>?op=upload_form</c>.</b> Both return 200 to a
    /// signed-in caller, which is what makes this worth pinning: the family page renders no upload
    /// form, so the base would scrape nothing and report "the session may have expired" — a sign-in
    /// problem the user cannot fix, for a session that is perfectly good.
    /// </summary>
    protected override string UploadFormUrl => Host + "/?op=upload";

    /// <summary>Guest 100 MB, registered 10 GB — both quoted by the host's own
    /// <c>?op=api_get_limits</c> for that session.</summary>
    public override long? MaxFileSizeFor(FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? AnonymousMaxFileSizeBytes : RegisteredMaxFileSizeBytes;

    /// <summary>Test seams — these three decide which paths this host takes, and none is observable
    /// from outside the family otherwise.</summary>
    internal bool UsesWebFormUploadForTests => UsesWebFormUpload;

    /// <inheritdoc cref="UsesWebFormUploadForTests"/>
    internal bool SupportsDirectLoginForTests => SupportsDirectLogin;

    /// <inheritdoc cref="UsesWebFormUploadForTests"/>
    internal string UploadFormUrlForTests => UploadFormUrl;

    /// <summary>Test seam for the username scrape — a host-specific override of a family default that
    /// silently returns nothing on this theme, which is the failure mode worth pinning.</summary>
    internal string? ParseAccountUsernameForTests(string html) => ParseAccountUsername(html);

    /// <summary>Test seam for the storage scrape, which now goes through the base: this pins that the
    /// <c>id="occupied"</c> bar this theme uses still reaches it.</summary>
    internal (long? Used, long? Quota) ParseStorageUsageForTests(string html) => ParseStorageUsage(html);

    /// <inheritdoc/>
    protected override string? ParseAccountUsername(string html)
    {
        Match m = _usernameRegex.Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Reads the node out of <c>?op=api_get_limits</c> rather than off a form, because this host
    /// renders no anonymous form to scrape. The query appended to the script is the family's own
    /// (<c>upload_type=file&amp;utype=anon</c>) — the exact request that was verified live.
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

        Match m = _serverUrlRegex.Match(xml);
        if (!m.Success)
        {
            if (LooksLikeCloudflareChallenge(xml))
            {
                return (null,
                    $"{Name}: Cloudflare is serving this client its \"Just a moment…\" challenge instead of "
                    + "the limits call. A managed challenge validates the browser itself (TLS fingerprint, "
                    + "JS execution), so no header or cookie sent from here can satisfy it.");
            }

            return (null, $"{Name}: ?op=api_get_limits carried no <ServerURL>: {Snippet(xml)}");
        }

        string node = m.Groups[1].Value.TrimEnd('/');
        return ($"{node}/upload.cgi?upload_type=file&utype=anon", null);
    }
}
