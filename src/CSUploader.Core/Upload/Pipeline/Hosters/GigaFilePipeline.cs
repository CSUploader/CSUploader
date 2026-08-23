// <copyright file="GigaFilePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// GigaFile (ギガファイル便, gigafile.nu) — anonymous, no account, and the largest per-file allowance
/// in the app by a wide margin: <b>300 GB</b>, kept up to <b>100 days</b>. Protocol read straight out
/// of the site's own <c>js/upload.js</c> + <c>gfupload-1.0.2.min.js</c> — no capture needed, the same
/// way Webshare's came out of its plupload script.
/// <list type="number">
///   <item><b>Pick up the node and the chunk size.</b> The homepage declares
///   <c>var server = "115.gigafile.nu"</c>, <c>var chunk_size = "100mb"</c> and
///   <c>var max_size = "300gb"</c>. The node ROTATES, so it is read per upload rather than pinned.</item>
///   <item><b>Send the chunks.</b> Multipart POST per chunk to <c>https://&lt;node&gt;/upload_chunk.php</c>
///   carrying <c>id</c>, <c>name</c>, <c>chunk</c> (0-based), <c>chunks</c> (total), <c>lifetime</c>
///   and the slice in <c>file</c>. Every chunk answers <c>{"status":0}</c>; a non-zero <c>status</c>
///   carries the host's own <c>message</c>.</item>
///   <item><b>The last chunk answers with the link.</b>
///   <c>{"status":0,"url":"https://&lt;node&gt;/&lt;code&gt;","delkey":"…","filename":"…"}</c> — note the
///   share URL is on the NODE's host, not the apex.</item>
/// </list>
/// <para>
/// <b>The upload id is ours to invent</b>, unlike upload.ee where the server must mint it: their
/// <c>uniqid()</c> is four random 32-bit words in hex, generated client-side. So a retry can safely
/// start over with a fresh id — nothing server-side exists until the first chunk lands.
/// </para>
/// <para>
/// <b>⚠ <c>lifetime</c> is the trap this family keeps setting.</b> Their slider offers
/// 3/5/7/14/30/60/100 days and the page ships <b>7</b>; omitting the field or copying the default
/// would quietly throw away 93 days of retention. This sends <b>100</b>, and the host confirms it —
/// the JWT in the reply decodes to a <c>d_expiry</c> 100 days out. Same shape as tmpfiles.org's
/// one-hour default, qu.ax's 30-day one and Litterbox's one-hour one.
/// </para>
/// <para>
/// <b>⚠ The chunks are tied together by COOKIES, and this app's handler doesn't keep any.</b> The
/// first chunk answers with <c>gfsid</c> (the upload session, which is what knows the destination
/// directory) and <c>Apache</c> (sticky routing to the backend holding the partial file). Send the
/// later chunks without them and the host says so, in Japanese, at the very end of the upload:
/// <c>保存先ディレクトリを取得できませんでした。Cookieが無効になっている可能性があります。</c>
/// ("couldn't get the destination directory — cookies may be disabled"). A single-chunk file never
/// shows this, which is exactly why it was worth uploading a two-chunk one before shipping.
/// </para>
/// <para>
/// The reply's <c>delkey</c> is the only way to delete an anonymous upload, so it is logged rather
/// than dropped — as upload.ee's killcode and Sendspace's delete link are.
/// </para>
/// </summary>
public sealed class GigaFilePipeline : IFileHosterPipeline
{
    private const string Host = "https://gigafile.nu";

    /// <summary>Their own <c>max_size</c>, decimal — the page says "300gb" and their parser reads it
    /// as 1024-based, but the figure is a round marketing number either way. Read as binary to match
    /// how <c>gfupload.parseSize</c> actually expands it.</summary>
    private const long MaxFileSizeBytes = 300L * 1024 * 1024 * 1024;

    /// <summary>Their own <c>chunk_size</c> ("100mb", 1024-based in their parser).</summary>
    private const int ChunkSizeBytes = 100 * 1024 * 1024;

    /// <summary>The longest retention the site offers (its slider's last division). The page's own
    /// default is 7 — see the class remarks.</summary>
    private const int LifetimeDays = 100;

    // var server = "115.gigafile.nu";
    private static readonly Regex _serverRegex = new(
        """var\s+server\s*=\s*["']([A-Za-z0-9.-]+\.gigafile\.nu)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Func<string, Task<string>>? _getOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, long, Task<HttpResponseSnapshot>>? _chunkOverride;

    public GigaFilePipeline()
    {
    }

    /// <summary>Test ctor — stubs the homepage GET and the per-chunk POST so the whole chunk loop
    /// runs without the network.</summary>
    internal GigaFilePipeline(
        Func<string, Task<string>> getOverride,
        Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, long, Task<HttpResponseSnapshot>> chunkOverride)
    {
        _getOverride = getOverride;
        _chunkOverride = chunkOverride;
    }

    public string Name => "GigaFile";

    /// <summary>Downloads are captcha-free: its support FAQ says pressing download starts it
    /// immediately, and a live probe's download page had no captcha (2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    /// <summary>100 days - the longest of its 3/5/7/14/30/60/100-day options, which this app sends and
    /// the host confirms (the JWT in the reply decodes to a <c>d_expiry</c> 100 days out). Its page
    /// ships 7, so copying the default would throw away 93 days.</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials) => FileRetention.DaysAfterUpload(100);

    public int? MaxFilesPerPackage => null;

    /// <summary>No accounts at all — the service has no login.</summary>
    public bool SupportsAnonymousUpload => true;

    /// <summary>GigaFile has no login anywhere on the site, so the Add Account dialog leaves it out
    /// of its hoster list — there is nothing to add.</summary>
    public bool SupportsAccounts => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds GigaFile's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Step 1: which node takes today's uploads ===
        string? node = null;
        string? nodeError = null;
        try
        {
            string html = _getOverride is not null
                ? await _getOverride(Host + "/")
                : await ctx.Handler.GetStringAsync(Host + "/", BrowserHeaders(), ctx.Cancellation);

            Match m = _serverRegex.Match(html);
            node = m.Success ? m.Groups[1].Value : null;
            if (node is null)
            {
                nodeError = $"GigaFile's homepage declared no upload node: {Snippet(html)}";
            }
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            nodeError = "GigaFile upload-node lookup failed: " + ex.Message;
        }

        if (node is null)
        {
            yield return new AttemptFailed(nodeError!, null);
            yield break;
        }

        // === Step 2: the chunks ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<(string? Url, string? DeleteKey, string? Error)> uploadTask = SendChunksAsync(ctx, node);
        _ = uploadTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= OnProgress;

        (string? url, string? deleteKey, string? error) = await uploadTask;
        if (url is null)
        {
            yield return new AttemptFailed(error ?? "GigaFile upload failed", null);
            yield break;
        }

        // The delkey is issued once, in this reply, and an anonymous upload has no account to manage
        // the file from — so log it rather than drop it.
        if (deleteKey is not null)
        {
            ctx.Logger.Log(this, LogType.Status, $"{Name}: delete key for {ctx.FileName} — {deleteKey}");
        }

        ctx.Logger.Log(
            this,
            LogType.Status,
            $"{Name}: {ctx.FileName} is kept for {LifetimeDays.ToString(CultureInfo.InvariantCulture)} days.");
        yield return new TransferCompleted(url);
    }

    /// <summary>The service has no accounts at all — there is nothing to check, and saying so beats a
    /// sign-in attempt that could only fail.</summary>
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
            "GigaFile has no accounts — use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>
    /// Walks the file in <see cref="ChunkSizeBytes"/> slices, POSTing each to the node. Returns the
    /// share URL from the FINAL chunk's reply — the intermediate ones carry only <c>status</c>.
    /// </summary>
    private async Task<(string? Url, string? DeleteKey, string? Error)> SendChunksAsync(AttemptContext ctx, string node)
    {
        string endpoint = $"https://{node}/upload_chunk.php";
        string id = NewUploadId();
        long fileSize = ctx.FileSize;

        // Their uploader sends a single chunk for an empty-or-smaller-than-a-chunk file, so the count
        // is never zero even for a 0-byte file.
        int totalChunks = fileSize <= ChunkSizeBytes ? 1 : (int)((fileSize + ChunkSizeBytes - 1) / ChunkSizeBytes);
        DateTime started = DateTime.Now;

        await using FileStream file = new(ctx.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long position = 0;

        // The chunk session lives in cookies the FIRST chunk hands back (gfsid + the Apache sticky
        // cookie). Our handler is built without a cookie container, so the jar is kept here and
        // replayed by hand — see the class remarks for what happens when it isn't.
        Dictionary<string, string> jar = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < totalChunks; index++)
        {
            long thisChunk = Math.Min(ChunkSizeBytes, fileSize - position);
            Dictionary<string, string> fields = new(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["name"] = ctx.FileName,
                ["chunk"] = index.ToString(CultureInfo.InvariantCulture),
                ["chunks"] = totalChunks.ToString(CultureInfo.InvariantCulture),
                ["lifetime"] = LifetimeDays.ToString(CultureInfo.InvariantCulture),
            };

            HttpResponseSnapshot response;
            if (_chunkOverride is not null)
            {
                response = await _chunkOverride(endpoint, fields, BrowserHeaders(jar), thisChunk);
            }
            else
            {
                ChunkSliceStream slice = new(file, thisChunk);
                response = await ctx.Handler.PostChunkMultipartAsync(
                    endpoint,
                    slice,
                    thisChunk,
                    basePosition: position,
                    totalFileSize: fileSize,
                    dateTimeStarted: started,
                    fileFieldName: "file",
                    // Their own uploader posts the slice as an unnamed Blob, which browsers send as
                    // filename="blob"; the real name travels in the `name` field above.
                    filePartName: "blob",
                    extraFields: fields,
                    headers: BrowserHeaders(jar),
                    speedBudget: ctx.SpeedBudget,
                    cancellationToken: ctx.Cancellation);
            }

            Collect(jar, response);

            (string? url, string? delkey, string? error) = ParseChunkResponse(response, index, totalChunks);
            if (error is not null)
            {
                return (null, null, error);
            }

            if (url is not null)
            {
                return (url, delkey, null);
            }

            position += thisChunk;
        }

        // Every chunk was accepted but none carried a link — the host changed its reply shape.
        return (null, null, "GigaFile accepted every chunk but returned no link.");
    }

    /// <summary>
    /// Reads one chunk reply. <c>status</c> is the host's own success flag — <b>0 means OK</b>, and
    /// anything else carries <c>message</c>. The final chunk adds <c>url</c> + <c>delkey</c>.
    /// Internal for testing.
    /// </summary>
    internal static (string? Url, string? DeleteKey, string? Error) ParseChunkResponse(HttpResponseSnapshot response, int index, int total)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, null, $"GigaFile rejected chunk {index + 1}/{total} (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, null, $"GigaFile's reply to chunk {index + 1}/{total} wasn't JSON: {Snippet(response.Body)}");
        }

        if (!root.TryGetProperty("status", out JsonElement status))
        {
            return (null, null, $"GigaFile's reply to chunk {index + 1}/{total} carried no status: {Snippet(response.Body)}");
        }

        // status is 0 on success. It arrives as a number, but a host that starts sending "0" as a
        // string shouldn't read as a failure.
        bool ok = status.ValueKind switch
        {
            JsonValueKind.Number => status.TryGetInt32(out int code) && code == 0,
            JsonValueKind.String => status.GetString() == "0",
            _ => false,
        };

        if (!ok)
        {
            string? message = root.TryGetProperty("message", out JsonElement m) ? m.GetString() : null;
            return (null, null, string.IsNullOrWhiteSpace(message)
                ? $"GigaFile refused chunk {index + 1}/{total}: {Snippet(response.Body)}"
                : $"GigaFile refused chunk {index + 1}/{total}: {message}");
        }

        string? url = root.TryGetProperty("url", out JsonElement u) ? u.GetString() : null;
        string? delkey = root.TryGetProperty("delkey", out JsonElement d) ? d.GetString() : null;
        return (string.IsNullOrWhiteSpace(url) ? null : url, string.IsNullOrWhiteSpace(delkey) ? null : delkey, null);
    }

    /// <summary>
    /// Their <c>uniqid()</c>: four random 32-bit words rendered in hex. Ours is 32 hex characters of
    /// the same entropy — the value only has to be unique to this upload, since the server has never
    /// seen it before the first chunk.
    /// </summary>
    private static string NewUploadId() => Guid.NewGuid().ToString("N");

    /// <summary>Adds a chunk reply's <c>Set-Cookie</c> values to the jar. Every cookie is kept: the
    /// two seen are <c>gfsid</c> and <c>Apache</c>, and a load balancer is free to add more.</summary>
    private static void Collect(Dictionary<string, string> jar, HttpResponseSnapshot response)
    {
        foreach (string raw in response.SetCookies)
        {
            int eq = raw.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string name = raw[..eq].Trim();
            int semi = raw.IndexOf(';', eq);
            string value = (semi < 0 ? raw[(eq + 1)..] : raw[(eq + 1)..semi]).Trim();
            if (value.Length > 0)
            {
                jar[name] = value;
            }
        }
    }

    private static Dictionary<string, string> BrowserHeaders(IReadOnlyDictionary<string, string>? jar = null)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
        };

        if (jar is { Count: > 0 })
        {
            headers["Cookie"] = string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        return headers;
    }

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
}
