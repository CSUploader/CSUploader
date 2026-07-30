// <copyright file="VikingFilePipeline.cs" company="CSUploader">
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
/// VikingFile (vikingfile.com) — anonymous upload over the host's own documented API
/// (<c>vikingfile.com/api</c>), verified end-to-end against the live service 2026-07-30 including a
/// real upload. Plain nginx, no Cloudflare, no cookies, no captcha, and no account: the API's
/// <c>user</c> parameter is simply sent empty.
/// <list type="number">
///   <item><b>Initiate.</b> <c>POST /api/get-upload-url</c> with the file <c>size</c> →
///   <c>{uploadId, key, partSize, numberParts, urls[]}</c>. The URLs are <b>Cloudflare R2 presigned
///   PUTs</b> — the same shape as storage.to's large-file path, so this reuses
///   <see cref="HttpHandler.PutChunkAsync"/> and <see cref="ChunkSliceStream"/>.</item>
///   <item><b>PUT each part</b> and keep its <c>ETag</c> response header. R2 returns the ETag quoted;
///   <see cref="HttpResponseSnapshot"/> already unquotes it.</item>
///   <item><b>Finalise.</b> <c>POST /api/complete-upload</c> with <c>key</c>, <c>uploadId</c>,
///   <c>name</c>, an empty <c>user</c>, and the parts as <c>parts[i][PartNumber]</c> /
///   <c>parts[i][ETag]</c> → <c>{name, size, hash, url}</c>. The share link is that <c>url</c>
///   (<c>vikingfile.com/f/&lt;hash&gt;</c>).</item>
/// </list>
/// <para>
/// <b><c>partSize</c> and <c>numberParts</c> are always read from the response, never assumed.</b> The
/// published docs show <c>partSize: 1073741824</c> (1 GiB) and the live service answered
/// <c>104857600</c> (100 MiB) — a 10× difference that would mis-slice every multi-part upload.
/// </para>
/// <para>
/// No client-side size cap: the service advertises "Unlimited filesize" for free/anonymous use, and
/// the part mechanism scales to any size by construction (the server decides how many parts). Free
/// uploads are <b>deleted 15 days after their last download</b>, which is the host's policy, not
/// something this pipeline can influence.
/// </para>
/// <para>
/// There is a second, legacy upload route (<c>GET /api/get-server</c> → single multipart POST of a
/// <c>file</c> field). It is not used: the parts route is the documented modern one, handles
/// arbitrarily large files, and gives per-part progress for free.
/// </para>
/// </summary>
public sealed class VikingFilePipeline : IFileHosterPipeline
{
    private const string Host = "https://vikingfile.com";
    private const string GetUploadUrlEndpoint = Host + "/api/get-upload-url";
    private const string CompleteUploadEndpoint = Host + "/api/complete-upload";

    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, int, HttpResponseSnapshot>? _putPartOverride;

    public VikingFilePipeline()
    {
    }

    /// <summary>Test ctor — drives the two API form POSTs and each part PUT from canned responses, so
    /// the slice/ETag/finalise chain runs without the network.</summary>
    internal VikingFilePipeline(
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postFormOverride,
        Func<string, int, HttpResponseSnapshot> putPartOverride)
    {
        _postFormOverride = postFormOverride;
        _putPartOverride = putPartOverride;
    }

    public string Name => "VikingFile";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>No cap — "Unlimited filesize" for anonymous use, and the server chooses the part
    /// count, so any size is expressible.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    /// <summary>VikingFile accepts uploads with no login — the API's <c>user</c> parameter is
    /// documented as "Empty for anonymous upload", so the wizard offers it as a built-in
    /// "Anonymous" option that needs no Accounts/Settings entry.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Step 1: initiate — the server sizes the parts and hands back presigned R2 PUTs ===
        (UploadInit? init, string? initError) = await GetUploadUrlAsync(ctx);
        if (init is null)
        {
            yield return new AttemptFailed(initError ?? "VikingFile upload initiation failed", null);
            yield break;
        }

        // === Step 2: PUT every part, then finalise ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<(string? Url, string? Error)> workTask = UploadPartsAndCompleteAsync(ctx, init);

        _ = workTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= onProgress;

        // A part-PUT transport fault propagates raw to AttemptRunner, which re-runs this pipeline from
        // a FRESH get-upload-url. That can't double-create: an R2 multipart upload only becomes a file
        // when complete-upload finalises it, so an abandoned one leaves nothing behind.
        (string? url, string? error) = await workTask;
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    /// <summary>
    /// VikingFile accounts aren't wired up — uploads use the anonymous path. Say so plainly rather
    /// than failing silently if someone adds a VikingFile account in Settings. (The API takes a
    /// <c>user</c> hash on every call, so an account path is a small addition when wanted: it would
    /// also unlock <c>delete-file</c> and <c>rename-file</c>.)
    /// </summary>
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
            "VikingFile login isn't supported yet — uploads use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>What <c>get-upload-url</c> hands back: the R2 multipart handle plus one presigned PUT
    /// per part.</summary>
    internal sealed record UploadInit(string Key, string UploadId, long PartSize, IReadOnlyList<string> PartUrls);

    private async Task<(UploadInit? Init, string? Error)> GetUploadUrlAsync(AttemptContext ctx)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["size"] = ctx.FileSize.ToString(CultureInfo.InvariantCulture),
        };

        HttpResponseSnapshot snap;
        try
        {
            snap = await PostFormAsync(ctx, GetUploadUrlEndpoint, form);
        }
        catch (Exception ex)
        {
            return (null, "VikingFile get-upload-url request failed: " + ex.Message);
        }

        return TryReadUploadInit(snap.Body) is { } init
            ? (init, null)
            : (null, $"VikingFile get-upload-url returned no usable upload handle (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
    }

    /// <summary>
    /// Parses <c>{uploadId, key, partSize, numberParts, urls[]}</c>. Null unless every piece needed to
    /// slice and finalise the upload is present and self-consistent — in particular <c>urls</c> must
    /// be non-empty, since zero parts would "succeed" without sending a byte. Internal for testing.
    /// </summary>
    internal static UploadInit? TryReadUploadInit(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? key = ReadString(doc.RootElement, "key");
            string? uploadId = ReadString(doc.RootElement, "uploadId");
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(uploadId))
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("partSize", out JsonElement sizeEl)
                || !TryReadInt64(sizeEl, out long partSize)
                || partSize <= 0)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("urls", out JsonElement urlsEl) || urlsEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<string> urls = [];
            foreach (JsonElement u in urlsEl.EnumerateArray())
            {
                if (u.ValueKind != JsonValueKind.String || u.GetString() is not { Length: > 0 } url)
                {
                    return null;
                }

                urls.Add(url);
            }

            return urls.Count == 0 ? null : new UploadInit(key, uploadId, partSize, urls);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>PUTs every part, collects the ETags, then finalises via complete-upload.</summary>
    private async Task<(string? Url, string? Error)> UploadPartsAndCompleteAsync(AttemptContext ctx, UploadInit init)
    {
        long total = ctx.FileSize;
        DateTime started = DateTime.Now;
        (int PartNumber, string ETag)[] parts = new (int, string)[init.PartUrls.Count];

        await using FileStream? fs = _putPartOverride is null
            ? new FileStream(ctx.FilePath, FileMode.Open, FileAccess.Read)
            : null;

        for (int i = 0; i < init.PartUrls.Count; i++)
        {
            int partNumber = i + 1;
            long basePos = (long)i * init.PartSize;
            long len = Math.Min(init.PartSize, total - basePos);

            // A part-PUT transport fault (or cancellation) is left to THROW so it reaches the retry
            // layer raw — nothing is committed until complete-upload, so re-running from a fresh
            // get-upload-url is safe and orphans only an unfinalised R2 multipart.
            HttpResponseSnapshot resp = _putPartOverride is not null
                ? _putPartOverride(init.PartUrls[i], partNumber)
                : await ctx.Handler.PutChunkAsync(
                    init.PartUrls[i], new ChunkSliceStream(fs!, len), len, basePos, total, started,
                    headers: null, ctx.SpeedLimitProvider, ctx.Cancellation);

            if (resp.StatusCode is < 200 or >= 300)
            {
                return (null, $"VikingFile R2 part {partNumber} rejected (HTTP {resp.StatusCode}): {Snippet(resp.Body)}");
            }

            if (string.IsNullOrEmpty(resp.ETag))
            {
                // Without every ETag, complete-upload cannot finalise the multipart at all.
                return (null, $"VikingFile R2 part {partNumber} returned no ETag");
            }

            parts[i] = (partNumber, resp.ETag);
        }

        return await CompleteUploadAsync(ctx, init, parts);
    }

    private async Task<(string? Url, string? Error)> CompleteUploadAsync(
        AttemptContext ctx, UploadInit init, (int PartNumber, string ETag)[] parts)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["key"] = init.Key,
            ["uploadId"] = init.UploadId,
            ["name"] = ctx.FileName,

            // Documented as "user's hash. Empty for anonymous upload."
            ["user"] = string.Empty,
        };

        for (int i = 0; i < parts.Length; i++)
        {
            form[$"parts[{i.ToString(CultureInfo.InvariantCulture)}][PartNumber]"] = parts[i].PartNumber.ToString(CultureInfo.InvariantCulture);
            form[$"parts[{i.ToString(CultureInfo.InvariantCulture)}][ETag]"] = parts[i].ETag;
        }

        HttpResponseSnapshot snap;
        try
        {
            snap = await PostFormAsync(ctx, CompleteUploadEndpoint, form);
        }
        catch (Exception ex)
        {
            return (null, "VikingFile complete-upload request failed: " + ex.Message);
        }

        return TryReadCompletedUrl(snap.Body) is { } url
            ? (url, null)
            : (null, $"VikingFile complete-upload returned no download link (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
    }

    /// <summary>
    /// Reads the share link out of <c>{"name","size","hash","url"}</c>, preferring the server's own
    /// <c>url</c> and falling back to building one from <c>hash</c>. Note <c>size</c> comes back as a
    /// STRING ("4096"), so nothing here assumes numeric JSON types. Internal for testing.
    /// </summary>
    internal static string? TryReadCompletedUrl(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (ReadString(doc.RootElement, "url") is { Length: > 0 } url
                && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            return ReadString(doc.RootElement, "hash") is { Length: > 0 } hash ? $"{Host}/f/{hash}" : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>Reads an integer that the API may express as a JSON number OR as a string — it does
    /// exactly that for <c>size</c> in the complete-upload response, so don't trust the type here.</summary>
    private static bool TryReadInt64(JsonElement el, out long value)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                return el.TryGetInt64(out value);
            case JsonValueKind.String:
                return long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }

    private Task<HttpResponseSnapshot> PostFormAsync(AttemptContext ctx, string url, IReadOnlyDictionary<string, string> form)
        => _postFormOverride is not null
            ? _postFormOverride(url, form)
            : ctx.Handler.PostFormAsync(url, form, headers: null, ctx.Cancellation);

    private static string Snippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string trimmed = body.Trim()
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }
}
