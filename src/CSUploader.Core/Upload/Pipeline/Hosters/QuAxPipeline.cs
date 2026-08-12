// <copyright file="QuAxPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// qu.ax — anonymous, no accounts, and the only host added in this batch whose files can be
/// <b>permanent</b>. One multipart POST to <c>/upload.php</c> with <c>files[]</c> and an
/// <c>expiry</c> →
/// <c>{"success":true,"files":[{"url":"https://qu.ax/&lt;code&gt;","expires":…,"hash":…,"size":…}]}</c>.
/// Verified with real bytes 2026-08-03.
/// <para>
/// <b>The expiry field is the whole point.</b> Its form offers 1 / 7 / 30 / 365 days and Permanent,
/// with <b>30 days checked by default</b> — and omitting the field takes that default. Measured:
/// omitted → <c>expires</c> 30 days out; <c>expiry=365</c> → a year; <c>expiry=-1</c> →
/// <c>expires: null</c>, i.e. no expiry at all. This ships <c>-1</c>. Third host running in this
/// pattern after Litterbox (<c>fileNameLength</c>) and tmpfiles (<c>expire</c>): on a drop host, an
/// optional field almost always encodes a worse default.
/// </para>
/// <para>
/// <b>⚠ It runs an ALLOWLIST, and release sets fall foul of it.</b> <c>.rar</c>, <c>.zip</c>,
/// <c>.7z</c>, <c>.tar</c>, <c>.gz</c> and <c>.part1.rar</c> upload fine, but <c>.r00</c>,
/// <c>.001</c>, <c>.sfv</c> and <c>.nfo</c> are refused with
/// <c>{"message":"file type is not allowed"}</c> (probed 2026-08-03). So a classic multi-part set is
/// only half-uploadable here while a modern <c>.partN.rar</c> set is fine.
/// <see cref="RejectionReason"/> checks locally, because the host refuses AFTER the bytes arrive.
/// </para>
/// <para>
/// 256 MB per file. Note it <b>de-duplicates by content hash</b> — re-uploading identical bytes
/// returns the same link, which makes a retry after a failed attempt harmless rather than duplicating.
/// </para>
/// </summary>
public sealed class QuAxPipeline : IFileHosterPipeline
{
    private const string Host = "https://qu.ax";
    private const string UploadUrl = Host + "/upload.php";

    /// <summary>"Max upload size is 256MB", per its own uploader.</summary>
    private const long MaxFileSizeBytes = 256L * 1024 * 1024;

    /// <summary>
    /// <c>-1</c> is the host's own "Permanent" option. Anything else is a countdown: its form defaults
    /// to 30 days and omitting the field silently accepts that.
    /// </summary>
    private const string Expiry = "-1";

    /// <summary>
    /// The allowlist its uploader publishes, verbatim (2026-08-03). An ALLOWLIST rather than a
    /// blocklist, so anything unlisted is refused — which is why this is checked rather than assumed.
    /// <c>.tar.gz2</c> is listed by the host and is almost certainly its typo for <c>.tar.bz2</c>;
    /// copied as published rather than corrected, since the server is what decides.
    /// </summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "webp", "avif", "svg",
        "mp4", "mov", "wmv", "mpeg", "mpg", "webm",
        "zip", "rar", "7z", "tar", "gz", "pdf", "txt",
    };

    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public QuAxPipeline()
    {
    }

    /// <summary>Test ctor — stubs the multipart upload so the orchestration runs without the network.</summary>
    internal QuAxPipeline(
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _uploadOverride = uploadOverride;
    }

    public string Name => "Qu.ax";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    /// <summary>Permanent - this app sends the host's own "Permanent" option (<c>expiry=-1</c>) and the
    /// reply carries <c>expires: null</c>. Omitting the field would take its 30-day default instead, so
    /// this is a property of what we ask for, not just of the host.</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials) => FileRetention.Permanent;

    public int? MaxFilesPerPackage => null;

    /// <summary>Anonymous is the only mode — there are no accounts.</summary>
    public bool SupportsAnonymousUpload => true;

    /// <summary>qu.ax has no login anywhere on the site, so the Add Account dialog leaves it out
    /// of its hoster list — there is nothing to add.</summary>
    public bool SupportsAccounts => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (RejectionReason(ctx.FileName, ctx.FileSize) is { } reason)
        {
            yield return new AttemptFailed(reason, null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx);
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

        HttpResponseSnapshot response = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(response);
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    /// <summary>No accounts exist, so there is nothing to validate.</summary>
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
            "qu.ax has no accounts — use the built-in Anonymous option in the upload wizard."));
    }

    /// <summary>
    /// Why this host will refuse the file, or null when it will take it. Checked locally because the
    /// allowlist is enforced only after the upload arrives — a <c>.r00</c> would otherwise spend the
    /// whole transfer to earn a refusal. Internal for testing.
    /// </summary>
    internal static string? RejectionReason(string fileName, long fileSize)
    {
        if (fileSize > MaxFileSizeBytes)
        {
            return $"File exceeds qu.ax's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                   + $"(this file is {ByteUnit.FromBytes(fileSize, ByteBase.Decimal).ToFriendlyString()}).";
        }

        return NameRejection(fileName);
    }

    /// <summary>
    /// The allowlist rule, on the interface so the UPLOAD WIZARD applies exactly what the upload
    /// would: files this host won't take are dropped from its column on the Summary step and listed
    /// in the warning panel <b>before the user presses Next</b>, instead of failing one by one later.
    /// Both callers share <see cref="NameRejection"/> — a second copy of the rule would drift.
    /// </summary>
    public string? RejectedFileExtensionReason(string fileName) => NameRejection(fileName);

    /// <summary>The allowlist itself. Internal for testing.</summary>
    internal static string? NameRejection(string fileName)
    {
        string ext = Path.GetExtension(fileName).TrimStart('.');
        if (AllowedExtensions.Contains(ext))
        {
            return null;
        }

        return $"qu.ax doesn't accept {(ext.Length > 0 ? "." + ext.ToLowerInvariant() : "extensionless")} files — it allows only "
               + $"{string.Join(", ", AllowedExtensions.Order(StringComparer.OrdinalIgnoreCase).Select(e => "." + e))}. "
               + "A .partN.rar set uploads fine; a classic .r00/.r01 set, .sfv and .nfo do not.";
    }

    /// <summary>
    /// Reads <c>{"success":true,"files":[{"url":…}]}</c>. <c>success</c> is checked explicitly so a
    /// failure envelope carrying a url can't read as one. Internal for testing.
    /// </summary>
    internal static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("success", out JsonElement success)
                && success.ValueKind == JsonValueKind.True
                && root.TryGetProperty("files", out JsonElement files)
                && files.ValueKind == JsonValueKind.Array
                && files.GetArrayLength() > 0
                && files[0].TryGetProperty("url", out JsonElement url)
                && url.GetString() is { Length: > 0 } link)
            {
                return (link, null);
            }

            // Its refusals are {"success":false,"message":"…"} — quote the host's own words.
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("message", out JsonElement message)
                && message.GetString() is { Length: > 0 } text)
            {
                return (null, $"qu.ax refused the file: {text}");
            }
        }
        catch (JsonException)
        {
            return (null, $"qu.ax returned an unreadable response (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        return (null, $"qu.ax returned no link (HTTP {response.StatusCode}): {Snippet(response.Body)}");
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

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx)
    {
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal)
        {
            ["expiry"] = Expiry,
        };

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
            ["Accept"] = "application/json",
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, UploadUrl, extraFields, headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            UploadUrl,
            fileFieldName: "files[]",
            extraFields: extraFields,
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }
}
