// <copyright file="FilebinPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Filebin (filebin.net) — anonymous, no accounts, and the only host here with a <b>published
/// OpenAPI spec</b> (<c>filebin.net/api.yaml</c>). The upload is one request:
/// <code>
/// POST https://filebin.net/&lt;bin&gt;/&lt;filename&gt;      (the raw file as the body)
///   -> 201 {"bin":{…,"expired_at":…},"file":{"filename":…,"bytes":…,"md5":…,"sha256":…}}
/// </code>
/// The bin is created by writing to it, so there is no setup call. Verified with real bytes
/// 2026-08-08; <c>.rar</c>, <c>.r00</c>, <c>.sfv</c>, <c>.nfo</c> and <c>.zip</c> are all accepted,
/// which matters because the spec documents a <b>403</b> for extensions it refuses.
/// <para>
/// <b>⚠ A BIN IS A PUBLIC NAMESPACE, and that is the whole security model.</b> Anyone who visits
/// <c>filebin.net/&lt;bin&gt;</c> sees every file in it — there is no password, token or per-file
/// secret. So each upload gets <b>its own bin with a 26-hex-character random name</b> from a
/// cryptographic RNG: a shared bin would expose the rest of a package to anyone given one link, and a
/// guessable name would expose it to anyone at all. This is a deliberate trade the user accepted;
/// it is weaker than a per-file unguessable id and unsuitable for anything private.
/// </para>
/// <para>
/// <b>Files expire after 7 days</b> — measured from the <c>expired_at</c> the host returns, not from
/// its marketing copy, which says six.
/// </para>
/// <para>
/// <b>Integrity is checked by the host when we can afford to ask.</b> If the app has already hashed
/// the file (the scheduler's MD5, hex), it is sent as the base64 <c>Content-MD5</c> the API wants and
/// a corrupted upload comes back <c>400 "MD5 checksum did not match"</c> instead of a 201 for a
/// damaged file. Hashing is not forced on for the sake of it: it costs a full extra read of the file,
/// which for a transfer service is a poor trade to make on the user's behalf.
/// </para>
/// </summary>
public sealed class FilebinPipeline : IFileHosterPipeline
{
    private const string Host = "https://filebin.net";

    /// <summary>13 random bytes → 26 hex characters. Long enough that a bin cannot be found by
    /// guessing, which is the only thing protecting its contents.</summary>
    private const int BinNameBytes = 13;

    private readonly Func<string, string, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public FilebinPipeline()
    {
    }

    /// <summary>Test ctor — stubs the one request this host needs.</summary>
    internal FilebinPipeline(Func<string, string, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
        => _uploadOverride = uploadOverride;

    public string Name => "Filebin";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>No documented per-file cap. The API's own failure for a full backend is a "storage
    /// limitation was reached, please retry later", which is a condition rather than a limit.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    public bool SupportsAnonymousUpload => true;

    /// <summary>Filebin has no accounts of any kind.</summary>
    public bool SupportsAccounts => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // One bin per file: a bin is public and lists everything inside it, so sharing one across a
        // package would mean handing over the whole package with any single link.
        string bin = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(BinNameBytes));
        string endpoint = $"{Host}/{bin}/{Uri.EscapeDataString(ctx.FileName)}";

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Accept"] = "application/json",
        };

        // The scheduler's hash is MD5 in hex; the API wants it base64. Only sent when the file was
        // hashed anyway — see the class remarks on why hashing isn't forced.
        if (ToBase64Md5(ctx.FileHash) is { } contentMd5)
        {
            headers["Content-MD5"] = contentMd5;
        }

        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> uploadTask = _uploadOverride is not null
            ? _uploadOverride(ctx.FilePath, endpoint, headers, ctx.SpeedLimitProvider)
            : ctx.Handler.UploadFileBodyAsync(
                HttpMethod.Post,
                ctx.FilePath,
                endpoint,
                "application/octet-stream",
                headers,
                ctx.SpeedLimitProvider,
                ctx.Cancellation);

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

        // A transport fault reaches the shared retry layer as a safe-to-retry body failure: a retry
        // mints a FRESH bin, so nothing is half-created and no bin is ever written to twice.
        HttpResponseSnapshot response = await uploadTask;

        (string? storedName, string? expiresAt, string? error) = ParseUploadResponse(response);
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        // The bin page is the only handle an anonymous upload has for deleting the file, so it is
        // logged rather than dropped — as upload.ee's killcode and GigaFile's delete key are.
        ctx.Logger.Log(this, LogType.Status, $"{Name}: {ctx.FileName} can be deleted from {Host}/{bin}");
        if (expiresAt is not null)
        {
            ctx.Logger.Log(this, LogType.Status, $"{Name}: {ctx.FileName} expires {expiresAt}.");
        }

        // The link uses the name the SERVER stored, not the one we sent: it sanitises what it must,
        // and a link built from our version would 404 whenever the two differ.
        yield return new TransferCompleted($"{Host}/{bin}/{Uri.EscapeDataString(storedName ?? ctx.FileName)}");
    }

    /// <summary>
    /// Reads the 201 envelope, or turns one of the API's documented failures into something the user
    /// can act on. Internal for testing.
    /// </summary>
    internal static (string? StoredName, string? ExpiresAt, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            // Straight from the spec's own response list, so the user is told which of these it was
            // rather than being handed a bare status code.
            string explanation = response.StatusCode switch
            {
                400 => "Filebin rejected the upload as invalid — if a checksum was sent, the file changed while it was being read",
                403 => "Filebin refuses this file type (or has blocked its content)",
                405 => "the bin is locked, expired or deleted and can't be written to",
                411 => "Filebin needs a Content-Length and didn't get a usable one",
                >= 500 => "Filebin is having trouble — its storage may be full, in which case it asks that you retry later",
                _ => "Filebin refused the upload",
            };

            return (null, null, $"{explanation} (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(response.Body).RootElement;
        }
        catch (JsonException)
        {
            return (null, null, $"Filebin's reply wasn't JSON: {Snippet(response.Body)}");
        }

        if (!root.TryGetProperty("file", out JsonElement file)
            || file.TryGetProperty("filename", out JsonElement name) is false
            || name.GetString() is not { Length: > 0 } stored)
        {
            return (null, null, $"Filebin accepted the upload but named no file: {Snippet(response.Body)}");
        }

        string? expires = root.TryGetProperty("bin", out JsonElement bin)
                          && bin.TryGetProperty("expired_at", out JsonElement exp)
            ? exp.GetString()
            : null;

        return (stored, expires, null);
    }

    /// <summary>Converts the scheduler's hex MD5 into the base64 the API's <c>Content-MD5</c> wants.
    /// Null in, null out — and null for anything that isn't a 32-character hex digest, since sending a
    /// malformed one would fail the upload for a reason that has nothing to do with the file.</summary>
    internal static string? ToBase64Md5(string? hexMd5)
    {
        if (hexMd5 is not { Length: 32 })
        {
            return null;
        }

        try
        {
            return Convert.ToBase64String(Convert.FromHexString(hexMd5));
        }
        catch (FormatException)
        {
            return null;
        }
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
            "Filebin has no accounts — use the built-in Anonymous option in the upload wizard."));
    }
}
