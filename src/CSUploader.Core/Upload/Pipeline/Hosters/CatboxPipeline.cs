// <copyright file="CatboxPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// catbox.moe upload pipeline — anonymous OR logged-in. A single multipart POST to the fixed
/// <c>https://catbox.moe/user/api.php</c> endpoint (no homepage scrape): fields
/// <c>reqtype=fileupload</c> + <c>fileToUpload=&lt;bytes&gt;</c>, plus — for an account — a
/// <c>userhash</c> that links the file to the account. The response body is the plain-text share URL
/// (<c>https://files.catbox.moe/&lt;code&gt;.&lt;ext&gt;</c>); a failure comes back as a non-URL error
/// string. The account's <c>userhash</c> is a permanent per-account token obtained once by logging in
/// (<c>POST /user/dologin.php</c>) and scraping the hidden <c>userhash</c> input off
/// <c>/user/view.php</c> — after that, uploads carry it with no cookie/session. No hashing. Verified
/// against live anonymous + logged-in captures 2026-07-03.
/// </summary>
public sealed partial class CatboxPipeline : IFileHosterPipeline
{
    private const string ApiUrl = "https://catbox.moe/user/api.php";
    private const string Host = "https://catbox.moe";
    private const string DoLoginUrl = Host + "/user/dologin.php";
    private const string ViewUrl = Host + "/user/view.php";
    private const string FilesPrefix = "https://files.catbox.moe/";

    /// <summary>Anonymous per-file cap — catbox.moe's documented 200 MB limit. Rejected client-side so
    /// an oversized file never wastes an upload; the server enforces its own limit regardless.</summary>
    private const long MaxAnonymousFileSizeBytes = 200L * 1024 * 1024;

    // The hidden field on /user/view.php: <input type="hidden" name="userhash" value="...">.
    private static readonly Regex _userhashRegex = MyRegex();

    // The account's permanent userhash, cached per credentials id. One login at a time per id so a
    // batch of N files against the same account does ONE login, not N.
    private readonly ConcurrentDictionary<int, string> _userhashByCredId = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();

    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _getSnapshotOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;

    public CatboxPipeline()
    {
    }

    /// <summary>Test ctor — stubs the multipart upload (and optionally the login POST + view.php GET
    /// for the account flow) so the orchestration runs without the network.</summary>
    internal CatboxPipeline(
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride,
        Func<string, IReadOnlyDictionary<string, string>?, HttpResponseSnapshot>? getSnapshotOverride = null,
        Func<string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot>? postFormOverride = null)
    {
        _uploadOverride = uploadOverride;
        _getSnapshotOverride = getSnapshotOverride is null ? null : (url, headers) => Task.FromResult(getSnapshotOverride(url, headers));
        _postFormOverride = postFormOverride is null ? null : (url, form) => Task.FromResult(postFormOverride(url, form));
    }

    public string Name => "Catbox";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxAnonymousFileSizeBytes;

    /// <summary>Permanent, and it is the reason to pick catbox over its own sibling: the host states
    /// files "are unlimited and never expire", which is exactly the trade Litterbox makes in reverse
    /// (five times the size, 72 hours to live).</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials) => FileRetention.Permanent;

    public int? MaxFilesPerPackage => null;

    /// <summary>catbox.moe accepts uploads with no account — the wizard offers it as a built-in
    /// "Anonymous" option. A logged-in account links uploads to it via the userhash.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxAnonymousFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds catbox.moe's {ByteUnit.FromBytes(MaxAnonymousFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Step 0 (account only): obtain the userhash that links the upload to the account ===
        string? userhash = null;
        if (!ctx.Credentials.IsAnonymous)
        {
            (string? gotUserhash, string? loginError) = await EnsureUserhashAsync(ctx);
            if (gotUserhash is null)
            {
                yield return new AttemptFailed(loginError ?? "catbox.moe login failed", null);
                yield break;
            }

            userhash = gotUserhash;
        }

        yield return new TransferStarted(ctx.FileSize);

        // Bridge HttpHandler.UploadProgress -> TransferProgress via an unbounded channel (can't yield
        // from inside the event handler) — same pattern as the other pipelines.
        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, userhash);
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

        // Let any transport fault propagate to the shared retry layer (AttemptRunner): a connect-phase
        // failure or mid-send abort arrives as a safe-to-retry UploadBodyTransferException, and re-running
        // never double-creates because the body never finished. A SERVER VERDICT does not throw
        // (UploadMultipartAsync returns the snapshot), so it flows through ParseUploadResponse below.
        HttpResponseSnapshot uploadResponse = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(uploadResponse);
        if (error is not null)
        {
            // An account upload rejected for a bad userhash may mean a stale cached one — drop it so the
            // next attempt re-logs-in. Remove only the value WE used (a concurrent attempt may have just
            // installed a fresh one). Anonymous uploads have nothing to invalidate.
            if (userhash is not null)
            {
                ((ICollection<KeyValuePair<int, string>>)_userhashByCredId)
                    .Remove(new KeyValuePair<int, string>(ctx.Credentials.Id, userhash));
            }

            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    /// <summary>
    /// Validates a catbox.moe account by logging in (<c>POST /user/dologin.php</c>) and scraping the
    /// account's <c>userhash</c> off <c>/user/view.php</c> — its presence is the success signal (a wrong
    /// login lands on an unauthenticated page with no userhash). catbox accounts have no storage quota
    /// (files are unlimited and never expire), so no usage is reported.
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        (string? userhash, string? error) = await LoginAndScrapeUserhashAsync(
            (url, form) => PostFormAsync(handler, url, form, ct),
            (url, headers) => GetSnapshotAsync(handler, url, headers, ct),
            username,
            password,
            ct);

        return userhash is null
            ? new AccountCheckResult(false, AccountType.Free, error ?? "catbox.moe login failed.")
            : new AccountCheckResult(true, AccountType.Free, "Signed in (Free)", DerivedUsername: username);
    }

    /// <summary>Returns the cached userhash for the account, logging in once (gated per credentials id)
    /// on a cache miss. Null + an error message when the login fails.</summary>
    private async Task<(string? Userhash, string? Error)> EnsureUserhashAsync(AttemptContext ctx)
    {
        int id = ctx.Credentials.Id;
        if (_userhashByCredId.TryGetValue(id, out string? cached))
        {
            return (cached, null);
        }

        SemaphoreSlim gate = _loginGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ctx.Cancellation).ConfigureAwait(false);
        try
        {
            if (_userhashByCredId.TryGetValue(id, out cached))
            {
                return (cached, null);
            }

            (string? userhash, string? error) = await LoginAndScrapeUserhashAsync(
                (url, form) => PostFormAsync(ctx.Handler, url, form, ctx.Cancellation),
                (url, headers) => GetSnapshotAsync(ctx.Handler, url, headers, ctx.Cancellation),
                ctx.Credentials.Username,
                ctx.Credentials.Password,
                ctx.Cancellation);
            if (userhash is null)
            {
                return (null, error);
            }

            _userhashByCredId[id] = userhash;
            return (userhash, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Logs in (<c>POST /user/dologin.php</c> — a url-encoded <c>username</c>/<c>password</c>/<c>submit</c>
    /// form; catbox sets a fresh authenticated <c>PHPSESSID</c> even without a prior session), then GETs
    /// <c>/user/view.php</c> with that cookie and scrapes the hidden <c>userhash</c> field. The userhash's
    /// presence is the validity check — a wrong login yields an unauthenticated page with none.
    /// </summary>
    private static async Task<(string? Userhash, string? Error)> LoginAndScrapeUserhashAsync(
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postForm,
        Func<string, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> getSnapshot,
        string? username,
        string? password,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return (null, "catbox.moe account needs a username and password.");
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["username"] = username,
            ["password"] = password,
            ["submit"] = "Login",
        };

        HttpResponseSnapshot loginSnap;
        try
        {
            loginSnap = await postForm(DoLoginUrl, form);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "catbox.moe login request failed: " + ex.Message);
        }

        string? phpsessid = ExtractCookieValue(loginSnap.SetCookies, "PHPSESSID");
        if (phpsessid is null)
        {
            return (null, $"catbox.moe login failed — check the username and password (HTTP {loginSnap.StatusCode}).");
        }

        Dictionary<string, string> headers = new(StringComparer.Ordinal) { ["Cookie"] = "PHPSESSID=" + phpsessid };
        HttpResponseSnapshot viewSnap;
        try
        {
            viewSnap = await getSnapshot(ViewUrl, headers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "catbox.moe account page fetch failed: " + ex.Message);
        }

        Match m = _userhashRegex.Match(viewSnap.Body);
        return m.Success
            ? (m.Groups[1].Value, null)
            : (null, "catbox.moe login failed — check the username and password.");
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string? userhash)
    {
        // Anonymous: just reqtype + the file. Account: also the userhash that links it to the account.
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal)
        {
            ["reqtype"] = "fileupload",
        };
        if (!string.IsNullOrEmpty(userhash))
        {
            extraFields["userhash"] = userhash;
        }

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = Host + "/",
            ["Accept"] = "application/json",
            // The site's Dropzone marks the upload as an XHR.
            ["X-Requested-With"] = "XMLHttpRequest",
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, ApiUrl, extraFields, headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            ApiUrl,
            fileFieldName: "fileToUpload",
            extraFields: extraFields,
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Success is HTTP 200 with a body that is the plain <c>https://files.catbox.moe/…</c> URL. A
    /// non-2xx, or a 2xx whose body isn't a catbox URL (catbox returns a plain error string like
    /// "Something went wrong.") surfaces as a failure with the body snippet.
    /// </summary>
    private static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"catbox.moe upload failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        string body = response.Body.Trim();
        if (body.StartsWith(FilesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (body, null);
        }

        // catbox echoes a plain-text reason on failure (e.g. a too-big file or a banned type).
        return (null, string.IsNullOrEmpty(body)
            ? $"catbox.moe upload returned an empty response (HTTP {response.StatusCode})."
            : $"catbox.moe upload was rejected: {Snippet(body)}");
    }

    /// <summary>Reads the value of the named cookie from a response's raw <c>Set-Cookie</c> lines
    /// (<c>name=value; attr=…</c>). Null when absent or empty.</summary>
    private static string? ExtractCookieValue(IReadOnlyList<string> setCookies, string name)
    {
        foreach (string raw in setCookies)
        {
            int eq = raw.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0 || !string.Equals(raw[..eq].Trim(), name, StringComparison.Ordinal))
            {
                continue;
            }

            int semi = raw.IndexOf(';', eq);
            string value = (semi < 0 ? raw[(eq + 1)..] : raw[(eq + 1)..semi]).Trim();
            if (value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }

    private Task<HttpResponseSnapshot> PostFormAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string> form, CancellationToken ct)
        => _postFormOverride is not null ? _postFormOverride(url, form) : handler.PostFormAsync(url, form, ct);

    private Task<HttpResponseSnapshot> GetSnapshotAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        => _getSnapshotOverride is not null ? _getSnapshotOverride(url, headers) : handler.GetSnapshotAsync(url, headers, ct);

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

    [GeneratedRegex("""name=["']userhash["']\s+value=["']([a-fA-F0-9]+)["']""", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
