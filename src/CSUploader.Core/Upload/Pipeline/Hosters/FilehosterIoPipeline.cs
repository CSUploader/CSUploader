// <copyright file="FilehosterIoPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Text.RegularExpressions;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// filehoster.io — the first host on <see cref="XfsProSessionPipeline"/> (xfspro chunked upload with a
/// session: <c>op=start_upload</c> → <c>put_chunk.cgi</c> → form-urlencoded <c>import_file</c>). The
/// protocol lives in the base; this is the host's name, address and figures.
/// <para>
/// Account-only, at a free-tier cap of 10 GB per file ("Max file size: 10GB" on its upload page — the
/// free REGISTERED allowance, not an anonymous one).
/// </para>
/// </summary>
public sealed class FilehosterIoPipeline : XfsProSessionPipeline
{
    // /account/ dashboard: <div ...>Used space</div> <div class="fs-4 ...">0.06</div> — a GiB figure.
    // Anchored on the value div's distinctive fs-4 class so the page's two "Used space" occurrences can't
    // confuse it (the decoy's following div has no fs-4), and tolerant of an inner span/icon before the
    // number.
    private static readonly Regex _usedSpaceRegex = new(
        """Used\s*space\s*</div>\s*<div[^>]*\bfs-4\b[^>]*>\s*(?:<[^>]+>\s*)*([0-9]+(?:\.[0-9]+)?)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public FilehosterIoPipeline()
    {
    }

    /// <summary>Test ctor (anonymous) — see the base.</summary>
    internal FilehosterIoPipeline(
        Func<string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postFormOverride,
        Func<string, string, long, long, long, Action<long, long>, HttpResponseSnapshot> chunkPutOverride)
        : base(postFormOverride, chunkPutOverride)
    {
    }

    /// <summary>Test ctor (account) — see the base.</summary>
    internal FilehosterIoPipeline(
        Func<string, IReadOnlyDictionary<string, string>?, HttpResponseSnapshot> getOverride,
        Func<string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postFormOverride,
        Func<string, string, long, long, long, Action<long, long>, HttpResponseSnapshot> chunkPutOverride)
        : base(getOverride, postFormOverride, chunkPutOverride)
    {
    }

    public override string Name => "Filehoster.io";

    /// <summary>From its own premium page (read 2026-08-12): free "5 days after last download",
    /// registered "60 days after last download", premium "Never".</summary>
    public override FileRetention RetentionFor(Dal.FileHosterLoginDto credentials)
        => credentials.IsAnonymous ? FileRetention.DaysAfterLastDownload(5)
            : credentials.AccountType == AccountType.Premium ? FileRetention.Permanent
            : FileRetention.DaysAfterLastDownload(60);

    protected override string Host => "https://filehoster.io";

    /// <summary>The brand lower-cases itself; keeps error text reading as the site does.</summary>
    protected override string DisplayName => "filehoster.io";

    /// <summary>10 GB, decimal to match the figure the upload page prints.</summary>
    public override long? MaxFileSize => 10L * 1000 * 1000 * 1000;

    /// <summary>Parses the dashboard's "Used space" value (a GiB figure, ceil-rounded to 2 decimals)
    /// into bytes. Null when the panel is absent or the number doesn't parse.</summary>
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
