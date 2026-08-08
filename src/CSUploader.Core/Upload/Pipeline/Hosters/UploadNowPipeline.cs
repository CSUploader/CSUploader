// <copyright file="UploadNowPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// UploadNow (uploadnow.io) — anonymous, <b>100 GB</b>, and the most involved protocol in the app:
/// a Firebase anonymous identity, the host's own signing service, and a Cloudflare R2 multipart
/// upload. From a browser capture 2026-08-08.
/// <list type="number">
///   <item><b>Become somebody.</b> <c>POST identitytoolkit.googleapis.com/v1/accounts:signUp</c> with
///   <c>{"returnSecureToken":true}</c> mints a <b>Firebase ANONYMOUS</b> account — an <c>idToken</c>
///   that authorises the API calls and a <c>localId</c> that becomes the storage key's prefix. Their
///   FAQ describes it as a guest account "stored in your browser", which is exactly what it is.</item>
///   <item><b>Make a folder, then declare the file.</b> <c>POST /api/file/folders</c> →
///   <c>{"id":"…"}</c>, then <c>POST /api/file/files</c> with the name and size →
///   <c>{"ids":["…"],"bucketConfig":{…}}</c>. That config carries the R2 endpoint, the bucket, the
///   signing service's path and the access-key id.</item>
///   <item><b>Upload to R2 as a signed S3 multipart</b> — initiate, one <c>PUT</c> per part, then
///   complete with the collected ETags.</item>
///   <item><b>Tell the site.</b> <c>PUT /api/file/files/&lt;id&gt;/upload-done</c>.</item>
/// </list>
/// <para>
/// <b>The signing is the interesting part, and it needs no secret.</b> Their service signs on the
/// client's behalf: this app builds the SigV4 canonical request and string-to-sign itself, then
/// <c>GET</c>s <c>/signer/buckets/&lt;id&gt;/sign-url?to_sign=…&amp;datetime=…</c>, which returns the
/// hex signature to paste into the <c>Authorization</c> header. So there is no AWS secret key here,
/// and — worth noting — <b>the signer itself requires no authentication</b>; only the
/// <c>/api/*</c> calls carry the bearer token.
/// </para>
/// <para>
/// Two details that make this cheaper than it looks: a part is sent with
/// <c>x-amz-content-sha256: UNSIGNED-PAYLOAD</c>, so the file never has to be SHA-256'd, and the only
/// body-derived header signed on a part is <c>Content-MD5</c> (the bucket config's
/// <c>computeContentMd5</c>).
/// </para>
/// <para>
/// <b>The share link is the FOLDER's</b> — <c>uploadnow.io/f/&lt;folderId&gt;</c>, which renders a
/// public "Shared folder" page listing what's inside. Verified in a browser with no session of the
/// uploader's. A folder is therefore created per file, as the site itself does, so every file gets
/// its own link.
/// </para>
/// <para>
/// <b>ACCOUNTS ARE PAID-ONLY here, so this ships anonymous-only</b> — see
/// <see cref="SupportsAccounts"/>.
/// </para>
/// </summary>
public sealed class UploadNowPipeline : IFileHosterPipeline
{
    private const string Host = "https://uploadnow.io";

    /// <summary>Firebase web API key, taken from the site's own bundle. This class of key is a public
    /// project identifier rather than a secret — it identifies which Firebase project to create the
    /// anonymous account in, and every visitor's browser sends it.</summary>
    private const string FirebaseWebKey = "AIzaSyB1SU4XZ9ryZjgtlYLU2yX2OBrAM6ajSWo";

    private const string SignUpUrl = "https://identitytoolkit.googleapis.com/v1/accounts:signUp?key=" + FirebaseWebKey;

    /// <summary>The figure the site's own sidebar shows a guest ("0 B / 100 GB").</summary>
    private const long MaxFileSizeBytes = 100L * 1024 * 1024 * 1024;

    /// <summary>Bytes per R2 part. R2 allows 10,000 parts, so this covers the full 100 GB with room to
    /// spare while keeping the per-part MD5 pass cheap.</summary>
    private const int PartSizeBytes = 64 * 1024 * 1024;

    private static readonly Regex _etagRegex = new("""<ETag>\s*(?:&quot;|")?([^<"&]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _uploadIdRegex = new("""<UploadId>([^<]+)</UploadId>""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>One anonymous identity per pipeline instance: a batch of N files mints ONE guest
    /// rather than N, the same reasoning gofile's cached guest account uses (there, creating one per
    /// file tripped a per-IP limit).</summary>
    private readonly SemaphoreSlim _identityGate = new(1, 1);
    private Identity? _identity;

    private readonly Func<HttpMethod, string, string?, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _apiOverride;
    private readonly Func<string, long, long, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _partOverride;

    public UploadNowPipeline()
    {
    }

    /// <summary>Test ctor — stubs the JSON/XML calls and the part PUTs so the whole orchestration runs
    /// without the network.</summary>
    internal UploadNowPipeline(
        Func<HttpMethod, string, string?, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> apiOverride,
        Func<string, long, long, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> partOverride)
    {
        _apiOverride = apiOverride;
        _partOverride = partOverride;
    }

    public string Name => "UploadNow";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    public bool SupportsAnonymousUpload => true;

    /// <summary>
    /// <b>Accounts exist here but every one of them is paid</b>, so there is no free credential a user
    /// could add and nothing for the app to verify — offering the host under Add Account could only
    /// produce a check that fails. Anonymous is the shipped path and it needs no account at all.
    /// </summary>
    public bool SupportsAccounts => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds UploadNow's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Steps 1-2: an identity, a folder and a declared file ===
        (Upload? upload, string? setupError) = await PrepareAsync(ctx);
        if (upload is null)
        {
            yield return new AttemptFailed(setupError!, null);
            yield break;
        }

        // === Step 3: the bytes, as a signed R2 multipart ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<string?> transferTask = TransferAsync(ctx, upload.Value);
        _ = transferTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= OnProgress;

        string? transferError = await transferTask;
        if (transferError is not null)
        {
            yield return new AttemptFailed(transferError, null);
            yield break;
        }

        // === Step 4: the file stays invisible until the site is told the bytes landed ===
        HttpResponseSnapshot done = await ApiAsync(
            ctx, HttpMethod.Put, $"{Host}/api/file/files/{upload.Value.FileId}/upload-done", "{}", upload.Value.Token);
        if (done.StatusCode is < 200 or >= 300)
        {
            yield return new AttemptFailed(
                $"UploadNow rejected the upload's completion (HTTP {done.StatusCode}): {Snippet(done.Body)}", null);
            yield break;
        }

        yield return new TransferCompleted($"{Host}/f/{upload.Value.FolderId}");
    }

    /// <summary>How many times a storage call is attempted before giving up.</summary>
    private const int StorageAttempts = 4;

    /// <summary>
    /// Runs one storage call, retrying while R2 answers <c>5xx</c>.
    /// <para>
    /// Its <c>InternalError</c> says "We encountered an internal error. Please try again." and means
    /// it: one was seen on a real <c>CreateMultipartUpload</c>. Every call here is safe to repeat —
    /// initiating twice just abandons an empty upload id, a part is addressed by number so re-sending
    /// overwrites, and completing is idempotent for the same parts.
    /// </para>
    /// <para>
    /// <paramref name="attempt"/> is re-invoked rather than the response replayed, because each
    /// attempt must be signed afresh: the signature covers <c>x-amz-date</c>, so a retry carrying the
    /// first attempt's timestamp would fail authentication instead of the thing that actually failed.
    /// </para>
    /// </summary>
    private static async Task<HttpResponseSnapshot> WithStorageRetryAsync(
        AttemptContext ctx,
        string what,
        Func<Task<HttpResponseSnapshot>> attempt)
    {
        HttpResponseSnapshot response = await attempt().ConfigureAwait(false);
        for (int i = 2; i <= StorageAttempts && response.StatusCode >= 500; i++)
        {
            ctx.Logger.Log(
                null,
                LogType.Status,
                $"UploadNow: storage answered HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)} to {what}; "
                + $"retrying ({i.ToString(CultureInfo.InvariantCulture)}/{StorageAttempts.ToString(CultureInfo.InvariantCulture)}).");

            await Task.Delay(TimeSpan.FromSeconds(i), ctx.Cancellation).ConfigureAwait(false);
            response = await attempt().ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>Mints (or reuses) the anonymous identity, creates this file's folder and declares the
    /// file, returning everything the transfer needs.</summary>
    private async Task<(Upload? Upload, string? Error)> PrepareAsync(AttemptContext ctx)
    {
        (Identity? identity, string? identityError) = await EnsureIdentityAsync(ctx);
        if (identity is null)
        {
            return (null, identityError);
        }

        // A folder per file: the share link is the FOLDER's, so one folder per upload is what gives
        // each file its own link — and it is what the site itself does.
        string folderName = Guid.NewGuid().ToString("N")[..8];
        HttpResponseSnapshot folder = await ApiAsync(
            ctx, HttpMethod.Post, $"{Host}/api/file/folders",
            $$"""{"name":"{{folderName}}","parentId":"/"}""", identity.Value.Token);

        if (folder.StatusCode is < 200 or >= 300 || ReadString(folder.Body, "id") is not { } folderId)
        {
            return (null, $"UploadNow wouldn't create a folder (HTTP {folder.StatusCode}): {Snippet(folder.Body)}");
        }

        string declare = JsonSerializer.Serialize(new
        {
            folderId,
            files = new[] { new { name = ctx.FileName, size = ctx.FileSize } },
        });

        HttpResponseSnapshot file = await ApiAsync(ctx, HttpMethod.Post, $"{Host}/api/file/files", declare, identity.Value.Token);
        if (file.StatusCode is < 200 or >= 300)
        {
            return (null, $"UploadNow wouldn't accept the file (HTTP {file.StatusCode}): {Snippet(file.Body)}");
        }

        (string? fileId, BucketConfig? config, string? parseError) = ParseDeclaredFile(file.Body);
        if (fileId is null || config is null)
        {
            return (null, parseError!);
        }

        return (new Upload(
            identity.Value.Token,
            folderId,
            fileId,
            config.Value,
            $"{config.Value.AwsUrl.TrimEnd('/')}/{identity.Value.LocalId}/{fileId}"), null);
    }

    /// <summary>Initiate → parts → complete. Returns null on success, else the failure to report.</summary>
    private async Task<string?> TransferAsync(AttemptContext ctx, Upload upload)
    {
        (string? uploadId, string? initError) = await InitiateAsync(ctx, upload);
        if (uploadId is null)
        {
            return initError;
        }

        List<string> etags = [];
        DateTime started = DateTime.Now;

        await using FileStream file = new(ctx.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long position = 0;
        int partNumber = 1;

        while (position < ctx.FileSize)
        {
            long length = Math.Min(PartSizeBytes, ctx.FileSize - position);

            // Content-MD5 is signed, so it has to be known before the part is sent — hence a read pass
            // over the slice ahead of the upload pass. Cheap next to the transfer itself.
            string contentMd5 = await ComputeMd5Async(file, position, length, ctx.Cancellation);

            string query = $"partNumber={partNumber.ToString(CultureInfo.InvariantCulture)}&uploadId={Uri.EscapeDataString(uploadId)}";
            long partOffset = position;
            string? partSignError = null;
            int number = partNumber;

            HttpResponseSnapshot part = await WithStorageRetryAsync(
                ctx,
                $"part {number.ToString(CultureInfo.InvariantCulture)}",
                async () =>
                {
                    string datetime = Timestamp();
                    Dictionary<string, string> signed = new(StringComparer.Ordinal)
                    {
                        ["content-md5"] = contentMd5,
                        ["host"] = new Uri(upload.ObjectUrl).Host,
                        ["x-amz-date"] = datetime,
                    };

                    (string? auth, partSignError) = await AuthorizeAsync(
                        ctx, upload, "PUT", upload.ObjectUrl, query, signed, "UNSIGNED-PAYLOAD", datetime).ConfigureAwait(false);
                    if (auth is null)
                    {
                        return new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>());
                    }

                    Dictionary<string, string> headers = new(StringComparer.Ordinal)
                    {
                        ["Authorization"] = auth,
                        ["x-amz-date"] = datetime,
                        ["x-amz-content-sha256"] = "UNSIGNED-PAYLOAD",
                        ["Content-MD5"] = contentMd5,
                    };

                    if (_partOverride is not null)
                    {
                        return await _partOverride($"{upload.ObjectUrl}?{query}", partOffset, length, headers).ConfigureAwait(false);
                    }

                    // Rewound per attempt: a retry has to re-send the same slice from the start.
                    file.Position = partOffset;
                    return await ctx.Handler.PutChunkAsync(
                        $"{upload.ObjectUrl}?{query}",
                        new ChunkSliceStream(file, length),
                        length,
                        basePosition: partOffset,
                        totalFileSize: ctx.FileSize,
                        dateTimeStarted: started,
                        headers: headers,
                        getBytesPerSecond: ctx.SpeedLimitProvider,
                        cancellationToken: ctx.Cancellation).ConfigureAwait(false);
                }).ConfigureAwait(false);

            if (partSignError is not null)
            {
                return partSignError;
            }

            if (part.StatusCode is < 200 or >= 300)
            {
                return $"UploadNow's storage refused part {partNumber.ToString(CultureInfo.InvariantCulture)} "
                       + $"(HTTP {part.StatusCode}): {Snippet(part.Body)}";
            }

            if (part.ETag is not { Length: > 0 } etag)
            {
                return $"UploadNow's storage returned no ETag for part {partNumber.ToString(CultureInfo.InvariantCulture)}, "
                       + "so the upload cannot be completed.";
            }

            etags.Add(etag.Trim('"'));
            position += length;
            partNumber++;
        }

        return await CompleteAsync(ctx, upload, uploadId, etags);
    }

    private async Task<(string? UploadId, string? Error)> InitiateAsync(AttemptContext ctx, Upload upload)
    {
        string? signError = null;
        HttpResponseSnapshot response = await WithStorageRetryAsync(ctx, "the upload's start", async () =>
        {
            string datetime = Timestamp();
            Dictionary<string, string> signed = new(StringComparer.Ordinal)
            {
                ["host"] = new Uri(upload.ObjectUrl).Host,
                ["x-amz-date"] = datetime,
            };

            (string? auth, signError) = await AuthorizeAsync(
                ctx, upload, "POST", upload.ObjectUrl, "uploads=", signed, EmptyBodySha256, datetime).ConfigureAwait(false);
            if (auth is null)
            {
                // Nothing to send; a 0 status stops the retry loop and signError carries the reason.
                return new HttpResponseSnapshot(0, string.Empty, Array.Empty<string>());
            }

            Dictionary<string, string> headers = new(StringComparer.Ordinal)
            {
                ["Authorization"] = auth,
                ["x-amz-date"] = datetime,
                ["x-amz-content-sha256"] = EmptyBodySha256,
            };

            return _apiOverride is not null
                ? await _apiOverride(HttpMethod.Post, $"{upload.ObjectUrl}?uploads", null, headers).ConfigureAwait(false)
                // No Content-Type, exactly as the browser's CreateMultipartUpload sends it.
                : await ctx.Handler.UploadBytesAsync(
                    HttpMethod.Post, $"{upload.ObjectUrl}?uploads", [], string.Empty, headers, cancellationToken: ctx.Cancellation)
                    .ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (signError is not null)
        {
            return (null, signError);
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"UploadNow's storage wouldn't start the upload (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        Match m = _uploadIdRegex.Match(response.Body);
        return m.Success
            ? (m.Groups[1].Value, null)
            : (null, $"UploadNow's storage returned no UploadId: {Snippet(response.Body)}");
    }

    private async Task<string?> CompleteAsync(AttemptContext ctx, Upload upload, string uploadId, List<string> etags)
    {
        StringBuilder xml = new("<CompleteMultipartUpload>");
        for (int i = 0; i < etags.Count; i++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<Part><PartNumber>{i + 1}</PartNumber><ETag>\"{etags[i]}\"</ETag></Part>");
        }

        xml.Append("</CompleteMultipartUpload>");
        byte[] body = Encoding.UTF8.GetBytes(xml.ToString());

        // The browser signs and sends "application/xml; charset=UTF-8"; our sender takes a bare media
        // type. Either is fine to R2 — what matters is that the value SIGNED is the value SENT, since
        // content-type is among this request's signed headers.
        const string ContentType = "application/xml";
        string datetime = Timestamp();
        Dictionary<string, string> signed = new(StringComparer.Ordinal)
        {
            ["content-type"] = ContentType,
            ["host"] = new Uri(upload.ObjectUrl).Host,
            ["x-amz-date"] = datetime,
        };

        string query = $"uploadId={Uri.EscapeDataString(uploadId)}";
        (string? auth, string? signError) = await AuthorizeAsync(
            ctx, upload, "POST", upload.ObjectUrl, query, signed, Sha256Hex(body), datetime);
        if (auth is null)
        {
            return signError;
        }

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Authorization"] = auth,
            ["x-amz-date"] = datetime,
            ["x-amz-content-sha256"] = Sha256Hex(body),
        };

        HttpResponseSnapshot response = await WithStorageRetryAsync(ctx, "the file's assembly", async () =>
            _apiOverride is not null
                ? await _apiOverride(HttpMethod.Post, $"{upload.ObjectUrl}?{query}", Encoding.UTF8.GetString(body), headers).ConfigureAwait(false)
                : await ctx.Handler.UploadBytesAsync(
                    HttpMethod.Post, $"{upload.ObjectUrl}?{query}", body, ContentType, headers, cancellationToken: ctx.Cancellation)
                    .ConfigureAwait(false)).ConfigureAwait(false);

        // R2, like S3, can report a failure inside a 200 on this call — so the body is checked too.
        if (response.StatusCode is < 200 or >= 300 || response.Body.Contains("<Error>", StringComparison.OrdinalIgnoreCase))
        {
            return $"UploadNow's storage wouldn't assemble the file (HTTP {response.StatusCode}): {Snippet(response.Body)}";
        }

        return null;
    }

    /// <summary>
    /// Builds the SigV4 canonical request and string-to-sign, has the host's signer sign it, and
    /// returns the finished <c>Authorization</c> header. No secret key is involved — that is the whole
    /// point of their signing service.
    /// </summary>
    private async Task<(string? Authorization, string? Error)> AuthorizeAsync(
        AttemptContext ctx,
        Upload upload,
        string method,
        string objectUrl,
        string query,
        IReadOnlyDictionary<string, string> signedHeaders,
        string payloadHash,
        string datetime)
    {
        string dateStamp = datetime[..8];
        string scope = $"{dateStamp}/{upload.Config.Region}/s3/aws4_request";

        IOrderedEnumerable<KeyValuePair<string, string>> ordered = signedHeaders.OrderBy(h => h.Key, StringComparer.Ordinal);
        string canonicalHeaders = string.Concat(ordered.Select(h => $"{h.Key}:{h.Value.Trim()}\n"));
        string signedList = string.Join(";", ordered.Select(h => h.Key));

        string canonicalRequest = string.Join(
            "\n",
            method,
            new Uri(objectUrl).AbsolutePath,
            query,
            canonicalHeaders,
            signedList,
            payloadHash);

        string stringToSign = string.Join(
            "\n", "AWS4-HMAC-SHA256", datetime, scope, Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));

        string signerUrl = upload.Config.SignerUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? upload.Config.SignerUrl
            : Host + upload.Config.SignerUrl;

        string url = $"{signerUrl}?to_sign={Uri.EscapeDataString(stringToSign)}&datetime={datetime}";

        HttpResponseSnapshot response;
        try
        {
            response = _apiOverride is not null
                ? await _apiOverride(HttpMethod.Get, url, null, null)
                : await ctx.Handler.GetSnapshotAsync(url, null, ctx.Cancellation);
        }
        catch (Exception ex)
        {
            return (null, $"UploadNow's signing service could not be reached: {ex.Message}");
        }

        string signature = response.Body.Trim();
        if (response.StatusCode is < 200 or >= 300 || signature.Length == 0)
        {
            return (null, $"UploadNow's signing service refused to sign the request (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        return ($"AWS4-HMAC-SHA256 Credential={upload.Config.AwsKey}/{scope}, SignedHeaders={signedList}, Signature={signature}", null);
    }

    private async Task<(Identity? Identity, string? Error)> EnsureIdentityAsync(AttemptContext ctx)
    {
        if (_identity is { } cached)
        {
            return (cached, null);
        }

        await _identityGate.WaitAsync(ctx.Cancellation).ConfigureAwait(false);
        try
        {
            if (_identity is { } raced)
            {
                return (raced, null);
            }

            HttpResponseSnapshot response;
            try
            {
                response = _apiOverride is not null
                    ? await _apiOverride(HttpMethod.Post, SignUpUrl, """{"returnSecureToken":true}""", null)
                    : await ctx.Handler.PostJsonAsync(SignUpUrl, """{"returnSecureToken":true}""", null, ctx.Cancellation);
            }
            catch (Exception ex)
            {
                return (null, $"UploadNow guest sign-up failed: {ex.Message}");
            }

            string? token = ReadString(response.Body, "idToken");
            string? localId = ReadString(response.Body, "localId");
            if (response.StatusCode is < 200 or >= 300 || token is null || localId is null)
            {
                return (null, $"UploadNow wouldn't issue a guest identity (HTTP {response.StatusCode}): {Snippet(response.Body)}");
            }

            _identity = new Identity(token, localId);
            return (_identity, null);
        }
        finally
        {
            _identityGate.Release();
        }
    }

    private Task<HttpResponseSnapshot> ApiAsync(AttemptContext ctx, HttpMethod method, string url, string? json, string token)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + token,
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
        };

        if (_apiOverride is not null)
        {
            return _apiOverride(method, url, json, headers);
        }

        byte[] body = Encoding.UTF8.GetBytes(json ?? string.Empty);
        return ctx.Handler.UploadBytesAsync(method, url, body, "application/json", headers, cancellationToken: ctx.Cancellation);
    }

    /// <summary>Reads the file id and the bucket configuration out of the declare-file reply. Internal
    /// for testing.</summary>
    internal static (string? FileId, BucketConfig? Config, string? Error) ParseDeclaredFile(string body)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            return (null, null, $"UploadNow's reply wasn't JSON: {Snippet(body)}");
        }

        if (!root.TryGetProperty("ids", out JsonElement ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0)
        {
            return (null, null, $"UploadNow returned no file id: {Snippet(body)}");
        }

        if (!root.TryGetProperty("bucketConfig", out JsonElement cfg))
        {
            return (null, null, $"UploadNow returned no bucket configuration: {Snippet(body)}");
        }

        string? awsUrl = Text(cfg, "aws_url");
        string? signerUrl = Text(cfg, "signerUrl");
        string? awsKey = Text(cfg, "aws_key");
        if (awsUrl is null || signerUrl is null || awsKey is null)
        {
            return (null, null, $"UploadNow's bucket configuration is missing a field: {Snippet(body)}");
        }

        return (ids[0].GetString(), new BucketConfig(awsUrl, signerUrl, awsKey, Text(cfg, "awsRegion") ?? "auto"), null);
    }

    private static string? Text(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? ReadString(string json, string name)
    {
        try
        {
            return Text(JsonDocument.Parse(json).RootElement, name);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>SHA-256 of an empty body — what S3 expects when a signed request carries none.</summary>
    private const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static string Timestamp()
        => DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    internal static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static async Task<string> ComputeMd5Async(FileStream file, long offset, long length, CancellationToken ct)
    {
        file.Position = offset;
        using MD5 md5 = MD5.Create();
        byte[] buffer = new byte[81920];
        long remaining = length;
        while (remaining > 0)
        {
            int wanted = (int)Math.Min(buffer.Length, remaining);
            int read = await file.ReadAsync(buffer.AsMemory(0, wanted), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            md5.TransformBlock(buffer, 0, read, null, 0);
            remaining -= read;
        }

        md5.TransformFinalBlock([], 0, 0);
        return Convert.ToBase64String(md5.Hash!);
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

    /// <summary>Everything the file-declare step hands back about where the bytes go.</summary>
    internal readonly record struct BucketConfig(string AwsUrl, string SignerUrl, string AwsKey, string Region);

    private readonly record struct Identity(string Token, string LocalId);

    private readonly record struct Upload(string Token, string FolderId, string FileId, BucketConfig Config, string ObjectUrl);

    /// <summary>No accounts are offered for this host — see <see cref="SupportsAccounts"/>.</summary>
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
            "UploadNow only sells paid accounts — use the built-in Anonymous option in the upload wizard."));
    }
}
