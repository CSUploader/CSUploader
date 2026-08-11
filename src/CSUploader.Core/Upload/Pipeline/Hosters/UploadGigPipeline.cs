// <copyright file="UploadGigPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// UploadGIG (uploadgig.com) — <b>account-only</b>, on the host's own published API, which is two calls:
/// <list type="number">
///   <item><c>POST /api/get_upload_action</c> with <c>user</c>/<c>pass</c> →
///   <c>{"code":"1","result":{"action":"http://&lt;ip&gt;:81/upload?identity=…","cmbs":10240}}</c>.</item>
///   <item><c>POST &lt;action&gt;</c> with the file as the RAW body and its name in a <c>Slug</c> header →
///   <c>{"0":{"ok":true,"url":"https://uploadgig.com/file/download/&lt;id&gt;/&lt;name&gt;",…}}</c>.</item>
/// </list>
/// <para>
/// ⚠ <b>The site's sign-in has a captcha; this path does not.</b> The API authenticates with the plain
/// username and password on every upload — no cookie, no token, no browser — so the host ships as an
/// ordinary username/password account rather than a WebView sign-in. Nothing is persisted beyond the
/// credentials the user typed.
/// </para>
/// <para>
/// ⚠ <b><c>cmbs</c> is the account's REMAINING storage, not a per-file limit</b> — the docs call it "the
/// max of allowed upload size", which is true but hides what it is. Measured: a fresh free account
/// answered 10240, and after a 2 MB file it answered 10238. So free storage is <b>10 GB total</b>, the
/// figure shrinks as files are stored, and it doubles as this attempt's real ceiling. It is read before
/// every upload and enforced there — which is also why the homepage's own <c>max_upload_limit = 102</c>
/// is ignored: a 120 MB file uploaded without complaint.
/// </para>
/// <para>
/// ⚠ <b>The action URL expires after 60 seconds</b> and points at <b>plain HTTP on a bare IP</b>
/// (<c>http://45.x.x.x:81</c>) — the host's design, not a fallback. It can therefore never be cached
/// between attempts, and it is fetched immediately before the bytes.
/// </para>
/// <para>
/// ⚠ <b>That fetch is a LOGIN, and logins are rate-limited</b>: too many in a short window earn
/// <c>{"code":"-3","result":"According to security reasons, you can't login a few minutes."}</c> — which
/// is why uploads are serialised to one at a time and the refusal is reported in the host's own words
/// rather than as a credentials problem.
/// </para>
/// </summary>
public sealed class UploadGigPipeline : IFileHosterPipeline
{
    private const string ActionApiUrl = "https://uploadgig.com/api/get_upload_action";

    /// <summary>Free storage, from the API's own figure on an empty account. Only a starting point for
    /// the wizard's warning — <see cref="RunAsync"/> enforces the live <c>cmbs</c>.</summary>
    private const long FreeStorageBytes = 10240L * 1024 * 1024;

    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public UploadGigPipeline()
    {
    }

    /// <summary>Test ctor — drives both calls from canned responses so every branch runs without the
    /// network or a real file.</summary>
    internal UploadGigPipeline(
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postFormOverride,
        Func<string, string, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _postFormOverride = postFormOverride;
        _uploadOverride = uploadOverride;
    }

    public string Name => "UploadGIG";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>10 GB — but see the type summary: this is the free tier's TOTAL storage, and the real
    /// ceiling for any one upload is whatever is left of it, which only the host can say. The wizard
    /// warns on this; the upload itself checks the live figure.</summary>
    public long? MaxFileSize => FreeStorageBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>Its API needs a username and password, and offers no guest route.</summary>
    public bool SupportsAnonymousUpload => false;

    /// <summary>
    /// One at a time. Every upload has to ask <c>/api/get_upload_action</c> for a 60-second URL first,
    /// that call is a login, and logins are rate-limited — a package that asked in parallel would spend
    /// its allowance on the queue rather than on files, and earn the whole account a few minutes of
    /// refusals. (Send.now taught the same lesson the expensive way, with a 60-minute lockout.)
    /// </summary>
    public int? MaxConcurrentUploadsFor(FileHosterLoginDto credentials)
    {
        _ = credentials;
        return 1;
    }

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ctx.Credentials.Username) || string.IsNullOrWhiteSpace(ctx.Credentials.Password))
        {
            yield return new AttemptFailed(
                "UploadGIG needs a username and password. Add the account under Settings → Accounts.",
                null);
            yield break;
        }

        // The action URL lives 60 seconds, so this is deliberately the last thing before the bytes.
        (UploadAction? action, string? actionError) = await GetUploadActionAsync(
            ctx.Handler, ctx.Credentials.Username, ctx.Credentials.Password!, ct);

        if (action is null)
        {
            yield return new AttemptFailed(actionError ?? "UploadGIG would not issue an upload address.", null);
            yield break;
        }

        // The host's own remaining-space figure, checked before sending rather than after: its docs ask
        // for exactly this, and the alternative is discovering a full account at the end of a 10 GB
        // transfer.
        if (ctx.FileSize > action.RemainingBytes)
        {
            yield return new AttemptFailed(
                $"{ctx.FileName} is {FormatSize(ctx.FileSize)} and the UploadGIG account has {FormatSize(action.RemainingBytes)} left "
                + "(free accounts get 10 GB). Delete something on uploadgig.com or upgrade the account.",
                null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

        // Bridge HttpHandler.UploadProgress -> TransferProgress via an unbounded channel (can't yield
        // from inside the event handler) — same pattern as the other streaming pipelines.
        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, action.Url);
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

        // A transport fault propagates to the shared retry layer: nothing is committed until the body is
        // fully sent, and a retry re-enters this method, which mints a FRESH action — the only thing that
        // could be reused here is the one thing that expires. Each re-run therefore spends another login
        // against the rate limit, which is the price of the retry working at all; a SERVER VERDICT (below)
        // does not throw and so is never replayed.
        HttpResponseSnapshot response = await uploadTask;

        (string? url, string? uploadError) = ParseUploadResponse(response);
        if (url is null)
        {
            yield return new AttemptFailed(uploadError ?? "UploadGIG upload failed.", null);
            yield break;
        }

        yield return new TransferCompleted(url);
    }

    /// <summary>
    /// Validates the account by asking for an upload address — the only thing this API does that proves
    /// a password, and the same call every upload makes. The reply also carries the account's remaining
    /// space, so the Accounts grid gets its usage figures from it for free.
    /// <para>
    /// The 60-second address it mints goes unused, which costs nothing; what it does cost is one login
    /// against the rate limit, so a user who checks an account repeatedly may briefly see the host's
    /// "can't login a few minutes" — reported in those words rather than as a bad password.
    /// </para>
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return new AccountCheckResult(false, AccountType.Free, "UploadGIG needs a username and password.");
        }

        (UploadAction? action, string? error) = await GetUploadActionAsync(handler, username, password, ct);
        if (action is null)
        {
            return new AccountCheckResult(false, AccountType.Free, error ?? "UploadGIG sign-in failed.");
        }

        // Used = the free tier's total minus what's left. An account with MORE than the free total is a
        // paid one, so reporting 10 GB as its quota would be a lie — leave the pair out and say so.
        bool looksFree = action.RemainingBytes <= FreeStorageBytes;
        return new AccountCheckResult(
            true,
            looksFree ? AccountType.Free : AccountType.Premium,
            looksFree
                ? $"Signed in to UploadGIG — {FormatSize(action.RemainingBytes)} of 10 GB free."
                : $"Signed in to UploadGIG — {FormatSize(action.RemainingBytes)} available.",
            DerivedUsername: username,
            StorageUsedBytes: looksFree ? FreeStorageBytes - action.RemainingBytes : null,
            StorageQuotaBytes: looksFree ? FreeStorageBytes : null);
    }

    /// <summary>
    /// Step 1 of the published API. Returns the 60-second upload address and the account's remaining
    /// space, or a message in the host's own words.
    /// </summary>
    private async Task<(UploadAction? Action, string? Error)> GetUploadActionAsync(
        HttpHandler handler, string username, string password, CancellationToken ct)
    {
        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["user"] = username,
            ["pass"] = password,
        };

        HttpResponseSnapshot response;
        try
        {
            response = _postFormOverride is not null
                ? await _postFormOverride(ActionApiUrl, form)
                : await handler.PostFormAsync(ActionApiUrl, form, null, ct);
        }
        catch (Exception ex)
        {
            return (null, $"UploadGIG sign-in request failed: {ex.Message}");
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"UploadGIG answered HTTP {response.StatusCode} to the sign-in.");
        }

        string code;
        JsonElement result;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            code = doc.RootElement.TryGetProperty("code", out JsonElement c) ? c.ToString() : string.Empty;
            result = doc.RootElement.TryGetProperty("result", out JsonElement r) ? r.Clone() : default;
        }
        catch (JsonException)
        {
            return (null, $"UploadGIG's sign-in answered something that isn't JSON: {Snippet(response.Body)}");
        }

        if (code != "1")
        {
            // Its refusals arrive as a plain string in `result`, and they are better written than
            // anything invented here — including the rate limit ("you can't login a few minutes"), which
            // is emphatically NOT a wrong password and must not be reported as one.
            string reason = result.ValueKind == JsonValueKind.String ? result.GetString() ?? string.Empty : string.Empty;
            return (null, reason.Length > 0
                ? $"UploadGIG: {reason}"
                : $"UploadGIG refused the sign-in (code {code}).");
        }

        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("action", out JsonElement actionElement)
            || actionElement.GetString() is not { Length: > 0 } actionUrl)
        {
            return (null, $"UploadGIG accepted the sign-in but named no upload address: {Snippet(response.Body)}");
        }

        // cmbs is in megabytes, and arrives as a number — but this API quotes `code`, so a quoted
        // figure is well within its habits. Absent or unreadable, treat as unknown rather than zero:
        // refusing every upload because a field moved would be worse than letting the node answer.
        // (Read by ValueKind rather than optimistically — GetString() on a number THROWS.)
        long remaining = long.MaxValue;
        if (result.TryGetProperty("cmbs", out JsonElement cmbs))
        {
            long megabytes = 0;
            bool readable = cmbs.ValueKind switch
            {
                JsonValueKind.Number => cmbs.TryGetInt64(out megabytes),
                JsonValueKind.String => long.TryParse(cmbs.GetString(), out megabytes),
                _ => false,
            };

            if (readable && megabytes >= 0)
            {
                remaining = megabytes * 1024 * 1024;
            }
        }

        return (new UploadAction(actionUrl, remaining), null);
    }

    /// <summary>Step 2: the file as the raw body, its name in <c>Slug</c>, exactly as the docs describe.</summary>
    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string actionUrl)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            // The ONLY place the filename travels — the body is the bytes and nothing else.
            ["Slug"] = EncodeSlug(ctx.FileName),
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, actionUrl, headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadFileBodyAsync(
            HttpMethod.Post,
            ctx.FilePath,
            actionUrl,
            contentType: "application/octet-stream",
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Percent-encodes the octets a <c>Slug</c> header may not carry, leaving an ASCII name byte for
    /// byte as the browser sends it.
    /// <para>
    /// ⚠ <b>Not a nicety — without it a file named <c>тест.rar</c> cannot be uploaded at all.</b> .NET
    /// refuses a non-ASCII header value outright (<c>HttpRequestException: Request headers must contain
    /// only ASCII characters</c>), and that throw happens inside the body-send, where the shared retry
    /// layer reads it as a transport fault: three full re-runs, three more rate-limited logins, and a
    /// "connection" message naming the wrong problem — the same disguise Buzzheavier's rejected
    /// filenames wore.
    /// </para>
    /// <para>
    /// Percent-encoding UTF-8 octets is what <c>Slug</c> is specified to do (RFC 5023 §9.7), and this
    /// host's raw-body-POST-plus-Slug shape is that convention. Whether it decodes them is untested —
    /// the alternative was refusing the file outright.
    /// </para>
    /// </summary>
    internal static string EncodeSlug(string fileName)
    {
        bool needsEncoding = false;
        foreach (char c in fileName)
        {
            if (c is < ' ' or > '~' or '%')
            {
                needsEncoding = true;
                break;
            }
        }

        if (!needsEncoding)
        {
            return fileName;
        }

        System.Text.StringBuilder sb = new(fileName.Length + 16);
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(fileName))
        {
            if (b is < 0x20 or > 0x7E || b == (byte)'%')
            {
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append((char)b);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads the node's verdict. ⚠ The envelope is an OBJECT keyed <c>"0"</c>, not an array — the docs'
    /// own PHP indexes it as one, which is the same thing in PHP and is not in .NET.
    /// </summary>
    private static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"UploadGIG's node answered HTTP {response.StatusCode}: {Snippet(response.Body)}");
        }

        JsonElement entry;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;
            entry = root.ValueKind switch
            {
                JsonValueKind.Object when root.TryGetProperty("0", out JsonElement first) => first.Clone(),
                JsonValueKind.Array when root.GetArrayLength() > 0 => root[0].Clone(),
                JsonValueKind.Object => root.Clone(),
                _ => default,
            };
        }
        catch (JsonException)
        {
            return (null, $"UploadGIG's node answered something that isn't JSON: {Snippet(response.Body)}");
        }

        if (entry.ValueKind != JsonValueKind.Object)
        {
            return (null, $"UploadGIG's node answered an unexpected shape: {Snippet(response.Body)}");
        }

        bool ok = entry.TryGetProperty("ok", out JsonElement okElement)
                  && (okElement.ValueKind == JsonValueKind.True
                      || (okElement.ValueKind == JsonValueKind.String && bool.TryParse(okElement.GetString(), out bool parsed) && parsed));

        string message = entry.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? string.Empty : string.Empty;

        if (!ok)
        {
            return (null, message.Length > 0 ? $"UploadGIG: {message}" : $"UploadGIG rejected the upload: {Snippet(response.Body)}");
        }

        // ⚠ The link is correct but not yet LIVE: the host says so itself ("file will be available in a
        // few minutes") and a GET seconds after a successful upload really does answer 404, while the
        // same URL serves a proper download page shortly after. Nothing to fix here — just don't let a
        // fresh 404 be read as a bad link.
        if (!entry.TryGetProperty("url", out JsonElement urlElement) || urlElement.GetString() is not { Length: > 0 } url)
        {
            // ok without a url would leave the file uploaded and unreachable; say so rather than
            // reporting a success the user cannot act on.
            return (null, $"UploadGIG reported success but named no link: {Snippet(response.Body)}");
        }

        return (url, null);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.##} MB",
        _ => $"{bytes / 1024.0:0.##} KB",
    };

    private static string Snippet(string body)
    {
        string s = body.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    /// <summary>The pair step 1 hands over: where to send the bytes, and how much room is left.</summary>
    private sealed record UploadAction(string Url, long RemainingBytes);
}
