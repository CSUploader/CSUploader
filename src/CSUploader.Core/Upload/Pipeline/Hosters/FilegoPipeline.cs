// <copyright file="FilegoPipeline.cs" company="CSUploader">
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
/// Filego (filego.io) — anonymous, <b>2 GB</b>, three calls. Like FileMirage, the protocol came out of
/// the site's own bundle (<c>/assets/js/bundle.js</c>) rather than a capture:
/// <list type="number">
///   <item><b>Declare.</b> <c>POST /api/upload/init</c> (multipart) with <c>name</c> and a
///   <c>files</c> JSON array of <c>{name,size,type}</c> → <c>{"id":…,"pw":…}</c>. The <c>pw</c> is a
///   per-upload write token, not an account credential.</item>
///   <item><b>Send.</b> <c>PUT /api/upload/file/&lt;id&gt;/&lt;index&gt;</c> with the raw bytes,
///   <c>X-Filego-Pw</c> and the file's content type. Whole file per request — no chunking.</item>
///   <item><b>Commit.</b> <c>POST /api/upload/save</c> with <c>id</c>, <c>pw</c>, <c>name</c> and
///   <c>expire</c>. Only after this does the link work; the share URL is
///   <c>https://filego.io/&lt;id&gt;</c>.</item>
/// </list>
/// <para>
/// <b>⚠ EVERY reply is HTTP 200, including the failures.</b> The envelope carries
/// <c>{"status":"ok"|"error","error":"…"}</c> and the status code never moves off 200, so a pipeline
/// that trusts the transport reports success for a refused upload and hands back a dead link. Each
/// step is therefore read from the envelope, not the status.
/// </para>
/// <para>
/// <b>Retention is a slider that defaults to the worse value.</b> Its page offers 1–30 days and starts
/// at <b>7</b>; this sends <b>30</b>, the longest the host allows, because the app's links outlive the
/// session that made them. Verified: the returned <c>expire</c> stamp came back exactly 30 days out.
/// </para>
/// <para>
/// The 2 GB cap is enforced only in the page's own JavaScript (<c>size &gt; 2147483648</c> → "Some
/// files are too large to upload"), so it is applied here up front rather than discovered after a
/// pointless 2 GB transfer. ⚠ <b><c>/api/upload/init</c> happily issues an id for a declared 10 GB</b>,
/// so that call is <b>not</b> evidence the host would take the bytes — raising this cap needs a real
/// oversized transfer, not another declare.
/// </para>
/// </summary>
public sealed class FilegoPipeline : IFileHosterPipeline
{
    private const string Host = "https://filego.io";
    private const string InitUrl = Host + "/api/upload/init";
    private const string SaveUrl = Host + "/api/upload/save";

    /// <summary>The page's own guard: <c>t.files[file].size &gt; 2147483648</c>.</summary>
    private const long MaxFileSizeBytes = 2_147_483_648;

    /// <summary>The maximum its expiry slider allows (<c>min="1" max="30"</c>), not the 7 it starts on.</summary>
    private const string ExpireDays = "30";

    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _putOverride;

    public FilegoPipeline()
    {
    }

    /// <summary>Test ctor — stubs the two form POSTs and the byte PUT.</summary>
    internal FilegoPipeline(
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postOverride,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> putOverride)
    {
        _postOverride = postOverride;
        _putOverride = putOverride;
    }

    public string Name => "Filego";

    /// <summary>Downloads are captcha-free: its whole app bundle contains no captcha and
    /// download is a direct navigation to /api/dl/file (bundle.js, 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    /// <summary>30 days, the longest its slider allows and what this app sends; the returned
    /// <c>expire</c> stamp came back exactly 30 days out. Its page starts at 7.</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials) => FileRetention.DaysAfterUpload(30);

    public int? MaxFilesPerPackage => null;

    public bool SupportsAnonymousUpload => true;

    /// <summary>The service has no accounts — its whole UI is upload, share, download.</summary>
    public bool SupportsAccounts => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds Filego's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === 1. declare the file, get an id + write token ===
        (string? id, string? pw, string? initError) = await InitAsync(ctx);
        if (id is null || pw is null)
        {
            yield return new AttemptFailed(initError ?? "Filego wouldn't start the upload.", null);
            yield break;
        }

        // === 2. the bytes ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> putTask = SendBytesAsync(ctx, id, pw);
        _ = putTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= OnProgress;

        string? putError = ReadEnvelopeError(await putTask, "sending the file");
        if (putError is not null)
        {
            yield return new AttemptFailed(putError, null);
            yield break;
        }

        // === 3. commit, or the link never works ===
        if (await SaveAsync(ctx, id, pw) is { } saveError)
        {
            yield return new AttemptFailed(saveError, null);
            yield break;
        }

        ctx.Logger.Log(this, LogType.Status, $"{Name}: {ctx.FileName} expires in {ExpireDays} days.");
        yield return new TransferCompleted($"{Host}/{id}");
    }

    /// <summary>Commits the upload. Until this lands the id resolves to nothing, so a failure here is
    /// a failed upload even though every byte is already on their disk.</summary>
    private async Task<string?> SaveAsync(AttemptContext ctx, string id, string pw)
    {
        try
        {
            HttpResponseSnapshot save = await PostFormAsync(ctx, SaveUrl, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["pw"] = pw,
                ["name"] = ctx.FileName,
                ["expire"] = ExpireDays,
            });

            return ReadEnvelopeError(save, "saving the upload");
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The bytes are up but unreferenced, so nothing is left half-shared — only unreachable.
            return $"Filego took the file but the upload couldn't be saved: {ex.Message}";
        }
    }

    private async Task<(string? Id, string? Pw, string? Error)> InitAsync(AttemptContext ctx)
    {
        // Its own client sends the whole set it is about to upload; one file per attempt here, so the
        // array has one entry and the PUT below uses index 0.
        string files = JsonSerializer.Serialize(new[]
        {
            new FileDeclaration(ctx.FileName, ctx.FileSize, "application/octet-stream"),
        });

        HttpResponseSnapshot response;
        try
        {
            response = await PostFormAsync(ctx, InitUrl, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = ctx.FileName,
                ["files"] = files,
            });
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, null, $"Filego upload setup failed: {ex.Message}");
        }

        return ParseInit(response);
    }

    /// <summary>Reads the <c>{id, pw}</c> the declare step returns. Internal for testing.</summary>
    internal static (string? Id, string? Pw, string? Error) ParseInit(HttpResponseSnapshot response)
    {
        if (ReadEnvelopeError(response, "starting the upload") is { } error)
        {
            return (null, null, error);
        }

        try
        {
            JsonElement root = JsonDocument.Parse(response.Body).RootElement;
            string? id = root.TryGetProperty("id", out JsonElement i) ? i.GetString() : null;
            string? pw = root.TryGetProperty("pw", out JsonElement p) ? p.GetString() : null;

            // A success envelope that names no upload is not one — the PUT would go to
            // /api/upload/file//0 and the link would be the site root.
            return string.IsNullOrEmpty(id) || string.IsNullOrEmpty(pw)
                ? (null, null, $"Filego accepted the request but issued no upload id: {Snippet(response.Body)}")
                : (id, pw, null);
        }
        catch (JsonException)
        {
            return (null, null, $"Filego's reply wasn't JSON: {Snippet(response.Body)}");
        }
    }

    /// <summary>
    /// Turns one of the API's envelopes into an error message, or null when it really did succeed.
    /// <para>
    /// <b>The status code is useless here</b> — this API answers 200 to everything and puts the verdict
    /// in <c>status</c>. It is still checked first, so a transport-level failure (a 502 from the edge,
    /// say) doesn't get parsed as an envelope. Internal for testing.
    /// </para>
    /// </summary>
    internal static string? ReadEnvelopeError(HttpResponseSnapshot response, string stage)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return $"Filego failed while {stage} (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}";
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return $"Filego's reply while {stage} wasn't JSON: {Snippet(response.Body)}";
        }

        string? status = root.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
        if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? message = root.TryGetProperty("error", out JsonElement e) ? e.GetString() : null;
        return string.IsNullOrWhiteSpace(message)
            ? $"Filego refused while {stage}: {Snippet(response.Body)}"
            : $"Filego refused while {stage}: {message}";
    }

    private Task<HttpResponseSnapshot> SendBytesAsync(AttemptContext ctx, string id, string pw)
    {
        // Index 0: one file per attempt, so this upload's file list has exactly one entry.
        string endpoint = $"{Host}/api/upload/file/{Uri.EscapeDataString(id)}/0";

        Dictionary<string, string> headers = BrowserHeaders();
        headers["X-Filego-Pw"] = pw;

        return _putOverride is not null
            ? _putOverride(endpoint, headers)
            : ctx.Handler.UploadFileBodyAsync(
                HttpMethod.Put,
                ctx.FilePath,
                endpoint,
                "application/octet-stream",
                headers,
                ctx.SpeedLimitProvider,
                ctx.Cancellation);
    }

    private Task<HttpResponseSnapshot> PostFormAsync(AttemptContext ctx, string url, Dictionary<string, string> fields)
        => _postOverride is not null
            ? _postOverride(url, fields)

            // Its own wrapper turns a plain object body into FormData, but the API takes a plain
            // urlencoded form just as happily (checked against the live endpoint), so this uses the
            // shared helper rather than hand-rolling a fields-only multipart body.
            : ctx.Handler.PostFormAsync(url, fields, BrowserHeaders(), ctx.Cancellation);

    private static Dictionary<string, string> BrowserHeaders() => new(StringComparer.Ordinal)
    {
        ["Origin"] = Host,
        ["Referer"] = Host + "/",
        ["Accept"] = "application/json, text/plain, */*",

        // Its own wrapper stamps every call with this; sent for parity with the site's client.
        ["X-Filego-Client"] = "filego.io v1.0-dev",
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

    /// <summary>No accounts exist on this service.</summary>
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
            "Filego has no accounts — use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>The shape its declare step wants each file in — lowercase keys, as its own client sends.</summary>
    private sealed record FileDeclaration(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size,
        [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type);
}
