// <copyright file="EasybytezPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Text.RegularExpressions;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// easybytez.org — the second host on <see cref="XfsProSessionPipeline"/>, from a browser capture of a
/// real signed-in upload 2026-08-03. It is filehoster.io's twin on the wire: <c>POST /</c>
/// <c>op=start_upload</c> → <c>{"url":"https://fs1.easybytez.org/cgi-bin","plugin":"xfspro"}</c> →
/// <c>put_chunk.cgi</c> with <c>X-Upload-SID</c> → <b>form-urlencoded</b> <c>api.cgi op=import_file</c>
/// carrying <c>sess_id=&lt;xfss&gt;</c> → <c>links.download_link</c>.
/// <para>
/// <b>Account-only, and worth spelling out because the page says otherwise.</b> Its upload page renders
/// the classic guest form — <c>utype=anon</c> beside an empty <c>sess_id</c> — but posting to its node
/// anonymously answers <c>[{"file_status":"uploads are not enabled for your account type"}]</c> (probed
/// 2026-08-03, both with and without <c>upload_type=file</c>). The form is decoration. That is why the
/// sweep which found ShareMods by reading exactly such a form could not settle this one: only the
/// upload's answer is evidence.
/// </para>
/// <para>
/// <b>Tiers, from its own comparison table:</b> guests 10 MB (hence the refusal), <b>registered 200 MB
/// per file with 10 GB of storage</b>, premium/pro 7000 MB. The registered figure is what ships; there
/// is no way to detect premium from the pages captured, so a paying user is capped conservatively
/// rather than optimistically — the same call Upstore and TeraBytez make.
/// </para>
/// <para>
/// Sign-in is a plain username/password form with <b>no captcha</b> (<c>op=login</c> → <c>xfss</c>), so
/// this needs no WebView.
/// </para>
/// </summary>
public sealed class EasybytezPipeline : XfsProSessionPipeline
{
    // /account/ dashboard: <div class="text-muted">Used space</div> <div class="fs-1 fw-bold …">0.00</div>
    // — a GiB figure, same semantics as filehoster.io's but a different theme (fs-1, and the label sits
    // in its own text-muted div). Anchored on the label so the neighbouring traffic/bandwidth cards,
    // which use identical markup, cannot be read as storage.
    private static readonly Regex _usedSpaceRegex = new(
        """Used\s*space\s*</div>\s*<div[^>]*\bfs-1\b[^>]*>\s*(?:<[^>]+>\s*)*([0-9]+(?:\.[0-9]+)?)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public EasybytezPipeline()
    {
    }

    /// <summary>Test ctor (anonymous) — see the base.</summary>
    internal EasybytezPipeline(
        Func<string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postFormOverride,
        Func<string, string, long, long, long, Action<long, long>, HttpResponseSnapshot> chunkPutOverride)
        : base(postFormOverride, chunkPutOverride)
    {
    }

    /// <summary>Test ctor (account) — see the base.</summary>
    internal EasybytezPipeline(
        Func<string, IReadOnlyDictionary<string, string>?, HttpResponseSnapshot> getOverride,
        Func<string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postFormOverride,
        Func<string, string, long, long, long, Action<long, long>, HttpResponseSnapshot> chunkPutOverride)
        : base(getOverride, postFormOverride, chunkPutOverride)
    {
    }

    public override string Name => "Easybytez";

    protected override string Host => "https://easybytez.org";

    /// <summary>200 MB on the registered tier, read as binary — the family's figures are 1024-based and
    /// the site prints "200 Mb".</summary>
    public override long? MaxFileSize => 200L * 1024 * 1024;

    /// <summary>Match the "Mb" its own pages print rather than restating it in decimal.</summary>
    protected override ByteBase CapUnits => ByteBase.Binary;

    /// <summary>
    /// The capture's set, verbatim: <b>no <c>file_size</c></b> (filehoster.io sends one) and
    /// <c>file_public=0</c> rather than 1. Replicated rather than assumed equivalent — this parser is
    /// field-presence sensitive.
    /// </summary>
    protected override Dictionary<string, string> BuildStartUploadForm(AttemptContext ctx) => new(StringComparer.Ordinal)
    {
        ["op"] = "start_upload",
        ["file_name"] = ctx.FileName,
        ["file_descr"] = string.Empty,
        ["file_public"] = "0",
    };

    /// <summary>The capture's finalise set — <c>file_public=0</c>, otherwise the family's.</summary>
    protected override Dictionary<string, string> BuildImportFileForm(AttemptContext ctx, string sid, string sessId) => new(StringComparer.Ordinal)
    {
        ["op"] = "import_file",
        ["sid"] = sid,
        ["fname"] = ctx.FileName,
        ["sess_id"] = sessId, // the account's xfss — attributes the upload to the account
        ["file_descr"] = string.Empty,
        ["file_public"] = "0",
        ["link_rcpt"] = string.Empty,
        ["link_pass"] = string.Empty,
        ["to_folder"] = string.Empty,
    };

    /// <summary>Parses the dashboard's "Used space" GiB figure into bytes. Null when absent.</summary>
    protected override long? ParseUsedSpaceBytes(string html) => ParseUsedSpace(html);

    /// <summary>Internal for testing — see <see cref="ParseUsedSpaceBytes"/>.</summary>
    internal static long? ParseUsedSpace(string html)
    {
        Match m = _usedSpaceRegex.Match(html);
        if (!m.Success
            || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double gib)
            || gib < 0)
        {
            return null;
        }

        return (long)(gib * (1L << 30));
    }
}
