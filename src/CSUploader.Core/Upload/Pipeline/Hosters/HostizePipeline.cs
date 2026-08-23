// <copyright file="HostizePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Hostize (www.hostize.com) — anonymous, keyless, <b>20 GB</b>, and the third host here on the
/// presigned-S3-multipart shape (after storage.to and VikingFile), so the byte path is theirs:
/// <list type="number">
///   <item><b>Ask for a ticket.</b> <c>POST /api/upload/request</c> with
///   <c>{"files":[{"name":…,"size":…}]}</c> → <c>{"id":…,"tickets":[{"partSize":…,"uploadId":…,
///   "partUrls":[{"partNumber":…,"url":…}]}]}</c>. The URLs are presigned S3 PUTs
///   (<c>s3.dynabic.com</c>).</item>
///   <item><b>PUT each part</b> and keep nothing — see below.</item>
///   <item><b>Complete.</b> <c>POST /api/upload/complete</c> with <c>{"shareId":…}</c> → the share
///   record. The link is <c>https://www.hostize.com/s/&lt;id&gt;</c>.</item>
/// </list>
/// <para>
/// <b>⚠ ITS DOCUMENTED API IS NOT THIS ONE.</b> <c>/api/<b>v1</b>/upload/request</c> is the published
/// route and answers <c>401 {"message":"Missing API key"}</c> — it is Pro-subscriber only. The site's
/// own uploader calls <c>/api/upload/request</c> (no <c>v1</c>), which needs no key at all. Reading
/// the docs alone would have written this host off as account-only.
/// </para>
/// <para>
/// <b>Unlike storage.to and VikingFile, the ETags are not needed:</b> <c>complete</c> takes only the
/// <c>shareId</c> and the server finalises the multipart itself. The parts are still checked for a
/// success status, because a silently dropped part would otherwise surface as a truncated file.
/// </para>
/// <para>
/// A capture of the site's own anonymous upload (2026-08-09) matches this exactly, with one field
/// not sent here: its request body carries <c>"concurrency":4</c>, which tells the server how many
/// parts the browser intends to PUT at once. These go up one at a time, so claiming four would be a
/// lie; omitting it is accepted and was verified by uploading.
/// </para>
/// <para>
/// <b>⚠ FREE LINKS LIVE 24 HOURS.</b> Straight from the service's own copy: "Files stay online for 24
/// hours on the Free plan, 30 days on the Standard plan, and forever on the Pro plan." The
/// <c>expiresAt</c> the complete call returns confirms it to the second, and the share page prints
/// "Expires in 24 hours". No request field changes it — <c>expiresIn</c>, <c>expiry</c>, <c>ttl</c>
/// and <c>retention</c> are all accepted and all ignored. The expiry is logged per upload so it is
/// visible at the time it matters rather than discovered a day later.
/// </para>
/// </summary>
public sealed class HostizePipeline : IFileHosterPipeline
{
    private const string Host = "https://www.hostize.com";
    private const string RequestUrl = Host + "/api/upload/request";
    private const string CompleteUrl = Host + "/api/upload/complete";

    /// <summary>"Max. size per upload: 20 GB" on the free plan, from the service's own pricing copy.</summary>
    private const long MaxFileSizeBytes = 20L * 1000 * 1000 * 1000;

    private readonly Func<string, string, Task<HttpResponseSnapshot>>? _postJsonOverride;
    private readonly PutPartHandler? _putPartOverride;

    public HostizePipeline()
    {
    }

    /// <summary>Test ctor — stubs the two JSON calls and the presigned part PUTs.</summary>
    internal HostizePipeline(
        Func<string, string, Task<HttpResponseSnapshot>> postJsonOverride,
        PutPartHandler putPartOverride)
    {
        _postJsonOverride = postJsonOverride;
        _putPartOverride = putPartOverride;
    }

    public string Name => "Hostize";

    /// <summary>Downloads are captcha-free: the anonymous download API 302s to a presigned file
    /// URL and the share-page chunk has no captcha (live probe, 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    /// <summary>24 hours, from the service's own copy - "Files stay online for 24 hours on the Free
    /// plan" - and confirmed to the second by the <c>expiresAt</c> its complete call returns. An
    /// ACCOUNT does not change it (measured: same 24 hours), which is why this ignores the credentials;
    /// longer retention is a paid plan this app doesn't model.</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials)
        => FileRetention.AfterUpload(TimeSpan.FromHours(24));

    public int? MaxFilesPerPackage => null;

    public bool SupportsAnonymousUpload => true;

    /// <summary>
    /// No account is offered, and captures of a real signed-in session (2026-08-09) give two
    /// independent reasons.
    /// <para>
    /// <b>There is no form to post.</b> Signing in is a <b>Keycloak OIDC authorization-code flow with
    /// PKCE</b> (<c>id.containerize.app/realms/hostize-com</c> → <c>/api/auth/callback/keycloak</c> →
    /// an <c>authjs</c> session cookie). That is the same class of blocker as the deferred cloud
    /// drives, not a username and password this app can send.
    /// </para>
    /// <para>
    /// <b>And it would buy nothing.</b> A signed-in FREE upload uses these very same three endpoints
    /// and comes back with <b>the same 24-hour expiry</b> — measured, <c>createdAt 01:56:33</c> →
    /// <c>expiresAt 01:56:39 the next day</c> — and the same cap. It only sets a <c>userId</c>, so the
    /// upload appears in the account's file list. Longer retention is a <i>paid</i> plan, not an
    /// account.
    /// </para>
    /// </summary>
    public bool SupportsAccounts => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds Hostize's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Decimal).ToFriendlyString()} per-upload limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === 1. the ticket ===
        (UploadTicket? ticket, string? requestError) = await RequestAsync(ctx);
        if (ticket is null)
        {
            yield return new AttemptFailed(requestError!, null);
            yield break;
        }

        // === 2. the parts ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<string?> partsTask = PutPartsAsync(ctx, ticket.Value);
        _ = partsTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= OnProgress;

        if (await partsTask is { } partError)
        {
            yield return new AttemptFailed(partError, null);
            yield break;
        }

        // === 3. complete, or the share is never published ===
        (string? expiresAt, string? completeError) = await CompleteAsync(ctx, ticket.Value.ShareId);
        if (completeError is not null)
        {
            yield return new AttemptFailed(completeError, null);
            yield break;
        }

        // Free links last a day, so say so at the moment the link is produced.
        ctx.Logger.Log(
            this,
            LogType.Status,
            expiresAt is null
                ? $"{Name}: {ctx.FileName} — free uploads stay online for 24 hours."
                : $"{Name}: {ctx.FileName} expires {expiresAt} (free uploads stay online for 24 hours).");

        yield return new TransferCompleted($"{Host}/s/{ticket.Value.ShareId}");
    }

    /// <summary>What one upload needs, all of it from the ticket call. <c>PartSize</c> is read from
    /// the response and never assumed — the same rule VikingFile's docs earned the hard way.
    /// <para>
    /// Parts keep the server's OWN <c>partNumber</c> rather than their array index. The two happen
    /// to agree today, but the offset a part covers is derived from its number, so silently
    /// renumbering them would mis-slice the file if Hostize ever returned them out of order.
    /// </para>
    /// </summary>
    internal readonly record struct UploadTicket(string ShareId, long PartSize, IReadOnlyList<(int PartNumber, string Url)> Parts);

    private async Task<(UploadTicket? Ticket, string? Error)> RequestAsync(AttemptContext ctx)
    {
        // The site's own uploader sends "concurrency":4. Omitting it was correct only while these
        // went up one at a time; claiming a number we do not honour would be the lie, not sending it.
        string json = JsonSerializer.Serialize(new
        {
            files = new[] { new { name = ctx.FileName, size = ctx.FileSize } },
            concurrency = ctx.MaxParallelParts,
        });

        HttpResponseSnapshot response;
        try
        {
            response = _postJsonOverride is not null
                ? await _postJsonOverride(RequestUrl, json)
                : await ctx.Handler.PostJsonAsync(RequestUrl, json, BrowserHeaders(), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"Hostize upload request failed: {ex.Message}");
        }

        return ParseTicket(response);
    }

    /// <summary>Reads the ticket call's reply. Internal for testing.</summary>
    internal static (UploadTicket? Ticket, string? Error) ParseTicket(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"Hostize wouldn't start the upload (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
        }

        try
        {
            JsonElement root = JsonDocument.Parse(response.Body).RootElement;

            if (!root.TryGetProperty("id", out JsonElement id)
                || id.GetString() is not { Length: > 0 } shareId
                || !root.TryGetProperty("tickets", out JsonElement tickets)
                || tickets.ValueKind != JsonValueKind.Array
                || tickets.GetArrayLength() == 0)
            {
                return (null, $"Hostize's reply carried no upload ticket: {Snippet(response.Body)}");
            }

            JsonElement ticket = tickets[0];
            long partSize = ticket.TryGetProperty("partSize", out JsonElement ps) && ps.TryGetInt64(out long size) ? size : 0;

            List<(int PartNumber, string Url)> urls = [];
            if (ticket.TryGetProperty("partUrls", out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    index++;
                    if (part.TryGetProperty("url", out JsonElement u) && u.GetString() is { Length: > 0 } url)
                    {
                        // The ticket carries an explicit partNumber. Falling back to array position
                        // when it is missing INVENTS one, which turns a malformed ticket into a
                        // plausible-looking duplicate — so a missing number is left as 0 and the
                        // validation below rejects the whole ticket.
                        int number = part.TryGetProperty("partNumber", out JsonElement pn) && pn.TryGetInt32(out int parsed)
                            ? parsed
                            : 0;
                        urls.Add((number, url));
                    }
                }
            }

            // A ticket with no parts, or a zero part size, would slice the file into nothing.
            return partSize <= 0 || urls.Count == 0
                ? (null, $"Hostize's upload ticket named no usable parts: {Snippet(response.Body)}")
                : (new UploadTicket(shareId, partSize, urls), null);
        }
        catch (JsonException)
        {
            return (null, $"Hostize's reply wasn't JSON: {Snippet(response.Body)}");
        }
    }

    /// <summary>
    /// PUTs each presigned part, up to <see cref="AttemptContext.MaxParallelParts"/> at once.
    /// Returns null on success, else which part was refused.
    /// <para>
    /// Unlike the other presigned hosts, no ETags are collected — <c>complete</c> takes only the
    /// share id and the server finalises the multipart itself. The parts are still checked for a
    /// success status, because a silently dropped part surfaces as a truncated file.
    /// </para>
    /// </summary>
    private async Task<string?> PutPartsAsync(AttemptContext ctx, UploadTicket ticket)
    {
        long total = ctx.FileSize;
        DateTime started = DateTime.Now;

        // Validate the ticket against the file before sending a byte: too few parts uploads a prefix
        // and still calls complete, publishing a truncated share that looks like a success.
        long expectedParts = Math.Max(1, ((total - 1) / ticket.PartSize) + 1);
        if (ticket.Parts.Count != expectedParts)
        {
            return $"Hostize returned {ticket.Parts.Count.ToString(CultureInfo.InvariantCulture)} part URL(s) "
                + $"for a {total.ToString(CultureInfo.InvariantCulture)}-byte file at "
                + $"{ticket.PartSize.ToString(CultureInfo.InvariantCulture)} bytes per part, where "
                + $"{expectedParts.ToString(CultureInfo.InvariantCulture)} were expected.";
        }

        // The COUNT being right is not enough. A ticket numbered [1, 1, 3] has the right size, sends
        // part 1's bytes to two different presigned URLs and never sends part 2 at all — and because
        // complete takes only the share id, the server would publish that corruption without a
        // word. The numbers must be exactly 1..n, each once.
        HashSet<int> numbers = [.. ticket.Parts.Select(p => p.PartNumber)];
        if (numbers.Count != ticket.Parts.Count || numbers.Any(n => n < 1 || n > expectedParts))
        {
            return "Hostize's ticket carried a malformed part map ("
                + string.Join(", ", ticket.Parts.Select(p => p.PartNumber.ToString(CultureInfo.InvariantCulture)))
                + $"); {expectedParts.ToString(CultureInfo.InvariantCulture)} parts numbered 1.."
                + $"{expectedParts.ToString(CultureInfo.InvariantCulture)} were expected.";
        }

        using FileSliceReader source = new(ctx.FilePath);
        PartProgressAggregator progress = new(
            ticket.Parts.Count,
            fileTotal => ctx.Handler.RaiseUploadProgress(new OperationProgressEventArgs(total, fileTotal, started)));

        // A transport fault is left to THROW so the shared retry layer sees it raw: nothing is
        // published until complete, so a retry from a fresh ticket orphans an unfinished multipart
        // and nothing more.
        PartResult[] results = await ParallelPartUploader.RunAsync(
            ticket.Parts.Count,
            ctx.MaxParallelParts,
            async (i, ct) =>
            {
                (int partNumber, string url) = ticket.Parts[i];

                // Offset from the part's OWN number, not its array index.
                long basePos = (partNumber - 1) * ticket.PartSize;
                long len = Math.Min(ticket.PartSize, total - basePos);
                Stream body = source.OpenSlice(basePos, len);

                HttpResponseSnapshot response = _putPartOverride is not null
                    ? await _putPartOverride(url, partNumber, basePos, len, body, bytes => progress.Report(i, bytes), ct)
                    : await ctx.Handler.PutChunkAsync(
                        url, body, len, basePos, total, started,
                        headers: null, ctx.SpeedBudget, ct, method: null,
                        reportPartProgress: bytes => progress.Report(i, bytes));

                return response.StatusCode is < 200 or >= 300
                    ? new PartResult(
                        partNumber,
                        null,
                        $"Hostize storage rejected part {partNumber.ToString(CultureInfo.InvariantCulture)}"
                        + $"/{ticket.Parts.Count.ToString(CultureInfo.InvariantCulture)} "
                        + $"(HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}")

                    // No ETag to keep: the server finalises on its own.
                    : new PartResult(partNumber, null, null);
            },
            ctx.Cancellation);

        return Array.Find(results, r => r.Error is not null) is { Error: not null } failed ? failed.Error : null;
    }

    /// <summary>Publishes the share. Unlike the other presigned hosts here this takes no ETags — the
    /// server finalises the multipart on its own.</summary>
    private async Task<(string? ExpiresAt, string? Error)> CompleteAsync(AttemptContext ctx, string shareId)
    {
        string json = JsonSerializer.Serialize(new { shareId });

        HttpResponseSnapshot response;
        try
        {
            response = _postJsonOverride is not null
                ? await _postJsonOverride(CompleteUrl, json)
                : await ctx.Handler.PostJsonAsync(CompleteUrl, json, BrowserHeaders(), ctx.Cancellation);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"Hostize took the file but the share couldn't be published: {ex.Message}");
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"Hostize wouldn't publish the share (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
        }

        return (ReadExpiry(response.Body), null);
    }

    /// <summary>Pulls <c>expiresAt</c> off the complete reply so the 24-hour life can be logged with
    /// the link. Internal for testing.</summary>
    internal static string? ReadExpiry(string body)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(body).RootElement;
            return root.TryGetProperty("expiresAt", out JsonElement e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> BrowserHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = Host,
        ["Referer"] = Host + "/",
        ["Accept"] = "application/json, text/plain, */*",
    };

    private static string Snippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty response)";
        }

        string trimmed = body.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }

    /// <summary>Uploading with an account needs a Pro subscription's API key, so none is offered.</summary>
    public Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = username;
        _ = password;
        _ = apiKey;
        _ = handler;
        _ = proxy;
        _ = ct;
        return Task.FromResult(new AccountCheckResult(
            false,
            AccountType.Free,
            "Hostize's upload API needs a Pro subscription — use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>
    /// Parts are order-independent here — server-issued presigned S3 part URLs — so they may be sent
    /// concurrently. Measured against live VikingFile on 2026-08-23: degree 8 reached 2.57x degree
    /// 1 and had not plateaued, so these hosts throttle per connection. Declared EXPLICITLY rather
    /// than relying on the interface default, which is not callable as a concrete-class member.
    /// The user's MaxParallelPartsPerFile setting caps this.
    /// </summary>
    public int MaxParallelPartsFor(Dal.FileHosterLoginDto credentials) => 8;
}
