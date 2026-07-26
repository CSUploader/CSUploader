// <copyright file="UpstorePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Upstore (upstore.net) upload pipeline — anonymous OR logged-in (email/password) uploads.
/// Verified against live captures 2026-06-28.
/// <list type="bullet">
///   <item><b>Per-upload server assignment.</b> GET <c>https://upstore.net/</c> returns a Dropzone
///   <c>&lt;form class="dropzone" action="https://dNN.upstore.net/newupload/"&gt;</c> whose upload
///   host (<c>dNN</c>) rotates — so it's scraped fresh for each file, never cached.</item>
///   <item><b>The POST is a multipart</b> to the scraped action: field <c>file</c> (the bytes) plus,
///   for an account, a <c>usid</c> field that links the upload to the account. The browser sends
///   <c>X-Requested-With: XMLHttpRequest</c> + <c>Accept: application/json</c> with the
///   <c>upstore.net</c> Origin/Referer; the upload node is direct nginx (not Cloudflare-fronted).</item>
///   <item><b>Login.</b> POST <c>https://upstore.net/account/login/</c>
///   (<c>email</c>/<c>password</c>/<c>send=Login</c>) returns a <c>Set-Cookie: usid=…</c> — that
///   <c>usid</c> value is the account credential POSTed on uploads. upstore.net isn't behind a
///   blocking Cloudflare challenge (the capture had no <c>Cf-Mitigated</c>), so this runs in C#
///   without a WebView. usid is cached in-memory per credentials id (re-login on a cache miss).</item>
///   <item><b>Result.</b> JSON <c>{"hash":"&lt;code&gt;",…}</c>; the shareable link is
///   <c>https://upstore.net/&lt;code&gt;</c>.</item>
/// </list>
/// No hashing. Storage: the <c>/account/</c> page shows "Used storage X MB" (free accounts have no
/// quota — Available is Unlimited); used is surfaced via <see cref="CheckAccountAsync"/> +
/// <see cref="IStorageRefreshablePipeline"/>.
/// </summary>
public sealed partial class UpstorePipeline : IFileHosterPipeline, IStorageRefreshablePipeline
{
    private const string Host = "https://upstore.net";
    private const string HomeUrl = Host + "/";
    private const string LoginUrl = Host + "/account/login/";
    private const string AccountUrl = Host + "/account/";
    private const string PublicUrlPrefix = Host + "/";

    /// <summary>Free/guest per-file cap — 1 GiB, matching the homepage Dropzone's
    /// <c>maxFilesize: 1024</c> (MiB). The server enforces it too: a 1.36 GiB upload came back
    /// <c>Error (Size1gb)</c> (live, 2026-07-26), which corrected the earlier belief that the widget
    /// value was only a client-side hint and the real server cap was 2 GiB. Premium (5 GB) isn't
    /// distinguished — premium accounts are capped at the free value, which only rejects a too-big
    /// file early; it never lets one waste bytes on a doomed upload.</summary>
    private const long FreeTierMaxFileSizeBytes = 1L * 1024 * 1024 * 1024;

    // The Dropzone form action points at the rotating upload host (dNN.upstore.net/newupload/).
    // Anchoring on "newupload" keeps us off the page's login/registration forms (/account/...),
    // and is robust to attribute ordering.
    private static readonly Regex _uploadActionRegex = MyRegex();

    // /account/ row: <td>Used storage</td><td>4.98 MB / 1 file</td>. The figure is a rounded display
    // value (no exact byte count is exposed), parsed best-effort. No quota is shown — free is Unlimited.
    private static readonly Regex _usedStorageRegex = new(
        """Used\s*storage\s*</td>\s*<td>\s*([0-9.]+)\s*([KMGT]?B)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // usid (the account upload credential) cached per credentials id. One login at a time per id so a
    // batch of N files against the same account does ONE login, not N.
    private readonly ConcurrentDictionary<int, string> _usidByCredId = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _loginGates = new();

    private readonly Func<string, Task<HttpResponseSnapshot>>? _getSnapshotOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public UpstorePipeline()
    {
    }

    /// <summary>Test ctor (anonymous) — drives the homepage GET and the multipart upload from canned
    /// responses so the scrape/parse logic can be exercised without the network.</summary>
    internal UpstorePipeline(
        Func<string, HttpResponseSnapshot> getSnapshotOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _getSnapshotOverride = url => Task.FromResult(getSnapshotOverride(url));
        _uploadOverride = uploadOverride;
    }

    /// <summary>Test ctor (account) — also stubs the login form POST so the logged-in flow
    /// (usid capture + usid-on-upload) can be exercised without the network.</summary>
    internal UpstorePipeline(
        Func<string, HttpResponseSnapshot> getSnapshotOverride,
        Func<string, IReadOnlyDictionary<string, string>, HttpResponseSnapshot> postFormOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _getSnapshotOverride = url => Task.FromResult(getSnapshotOverride(url));
        _postFormOverride = (url, form) => Task.FromResult(postFormOverride(url, form));
        _uploadOverride = uploadOverride;
    }

    public string Name => "Upstore";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => FreeTierMaxFileSizeBytes;

    public int? MaxFilesPerPackage => null;

    /// <summary>Upstore accepts uploads with no login — the wizard offers it as a built-in
    /// "Anonymous" option. Logged-in uploads (an added account) link to the account via usid.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Pre-check: per-file size cap (same 1 GiB for guests + free accounts) ===
        if (ctx.FileSize > FreeTierMaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds Upstore's free/guest {ByteUnit.FromBytes(FreeTierMaxFileSizeBytes, ByteBase.Binary).ToFriendlyString()} per-file limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Binary).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === Step 0 (account only): obtain the usid that links the upload to the account ===
        string? usid = null;
        if (!ctx.Credentials.IsAnonymous)
        {
            (string? gotUsid, string? loginError) = await EnsureUsidAsync(ctx);
            if (gotUsid is null)
            {
                yield return new AttemptFailed(loginError ?? "Upstore login failed", null);
                yield break;
            }

            usid = gotUsid;
        }

        // === Step 1: scrape the rotating upload server from the homepage form action ===
        (string? actionUrl, string? scrapeError) = await FetchUploadActionAsync(ctx);
        if (actionUrl is null)
        {
            yield return new AttemptFailed(scrapeError ?? "Upstore upload form not found", null);
            yield break;
        }

        // === Step 2: upload ===
        yield return new TransferStarted(ctx.FileSize);

        // Bridge HttpHandler.UploadProgress -> TransferProgress via an unbounded channel (can't
        // yield from inside the event handler) — same pattern as the other pipelines.
        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, actionUrl, usid);

        _ = uploadTask.ContinueWith(
            _ => progressChannel.Writer.Complete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (UploadEvent progressEv in progressChannel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progressEv;
        }

        ctx.Handler.UploadProgress -= onProgress;

        // Let any transport fault propagate out of RunAsync to the shared retry layer (AttemptRunner):
        // a connect-phase failure or mid-send abort arrives as a safe-to-retry UploadBodyTransferException,
        // and re-running this pipeline scrapes a FRESH rotating upload node — the right recovery, and it
        // never double-creates because the body never finished. A SERVER VERDICT does NOT throw
        // (UploadMultipartAsync returns the snapshot), so it still flows through ParseUploadResponse.
        HttpResponseSnapshot uploadResponse = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(uploadResponse);
        if (error is not null)
        {
            // An account upload that's rejected may mean a stale usid (the cached login expired) — drop
            // it so the next attempt re-logs-in. Remove only the usid WE used (value-matching overload):
            // a concurrent attempt may have just re-logged-in and installed a fresh one. Anonymous
            // uploads have nothing to invalidate.
            if (usid is not null)
            {
                ((ICollection<KeyValuePair<int, string>>)_usidByCredId)
                    .Remove(new KeyValuePair<int, string>(ctx.Credentials.Id, usid));
            }

            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    /// <summary>
    /// Validates an Upstore account by logging in (<c>POST /account/login/</c>): success is a
    /// <c>Set-Cookie: usid=…</c>. Then reads "Used storage" off <c>/account/</c> (best-effort; free
    /// accounts have no quota, so Available stays Unlimited).
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        (string? usid, string? upst, string? error) = await LoginAsync(
            (url, form) => PostFormAsync(handler, url, form, ct),
            username,
            password,
            ct);
        if (usid is null)
        {
            return new AccountCheckResult(false, AccountType.Free, error ?? "Upstore login failed.");
        }

        long? used = await TryReadUsedStorageAsync(handler, usid, upst, ct);
        return new AccountCheckResult(
            true,
            AccountType.Free,
            "Signed in (Free)",
            DerivedUsername: username,
            StorageUsedBytes: used,
            StorageQuotaBytes: null); // free accounts have no quota → Available shows Unlimited
    }

    /// <summary>
    /// Non-interactive storage refresh for the wizard's Summary page: a fresh credential login (no
    /// captcha/WebView) plus the same <c>/account/</c> "Used storage" read. Returns null on any
    /// failure (bad/expired creds, transport) so the caller keeps the last-known snapshot.
    /// </summary>
    public async Task<StorageUsage?> RefreshStorageAsync(FileHosterLoginDto credentials, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = proxy;

        (string? usid, string? upst, _) = await LoginAsync(
            (url, form) => PostFormAsync(handler, url, form, ct),
            credentials.Username,
            credentials.Password,
            ct);
        if (usid is null)
        {
            return null;
        }

        long? used = await TryReadUsedStorageAsync(handler, usid, upst, ct);
        return used is null ? null : new StorageUsage(used, null);
    }

    /// <summary>GETs the logged-in <c>/account/</c> page (auth = the <c>usid</c> + <c>upst</c> cookies)
    /// and scrapes the rounded "Used storage" figure. Returns null on any failure so a transient hiccup
    /// leaves Used blank rather than failing the account check.</summary>
    private async Task<long?> TryReadUsedStorageAsync(HttpHandler handler, string usid, string? upst, CancellationToken ct)
    {
        string cookie = "usid=" + usid + (string.IsNullOrEmpty(upst) ? string.Empty : "; upst=" + upst);
        Dictionary<string, string> headers = new(StringComparer.Ordinal) { ["Cookie"] = cookie };

        HttpResponseSnapshot snap;
        try
        {
            snap = await GetSnapshotAsync(handler, AccountUrl, headers, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        return ParseUsedStorage(snap.Body);
    }

    /// <summary>Parses "Used storage</td><td>4.98 MB / 1 file" into bytes (binary units). Null when
    /// the row is absent or the number/unit doesn't parse.</summary>
    internal static long? ParseUsedStorage(string html)
    {
        Match m = _usedStorageRegex.Match(html);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < 0)
        {
            return null;
        }

        long factor = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "B" => 1L,
            "KB" => 1024L,
            "MB" => 1024L * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            _ => 0L,
        };

        return factor == 0 ? null : (long)(value * factor);
    }

    /// <summary>Returns the cached usid for the account, logging in once (gated per credentials id) on
    /// a cache miss. Null + an error message when the login fails.</summary>
    private async Task<(string? Usid, string? Error)> EnsureUsidAsync(AttemptContext ctx)
    {
        int id = ctx.Credentials.Id;
        if (_usidByCredId.TryGetValue(id, out string? cached))
        {
            return (cached, null);
        }

        SemaphoreSlim gate = _loginGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ctx.Cancellation).ConfigureAwait(false);
        try
        {
            if (_usidByCredId.TryGetValue(id, out cached))
            {
                return (cached, null);
            }

            (string? usid, _, string? error) = await LoginAsync(
                (url, form) => PostFormAsync(ctx.Handler, url, form, ctx.Cancellation),
                ctx.Credentials.Username,
                ctx.Credentials.Password,
                ctx.Cancellation);
            if (usid is null)
            {
                return (null, error);
            }

            _usidByCredId[id] = usid;
            return (usid, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>POSTs the login form and pulls the account credentials out of the response's
    /// <c>Set-Cookie</c>: <c>usid</c> (the upload credential) and <c>upst</c> (the session cookie the
    /// <c>/account/</c> page needs). A wrong email/password re-renders the login page with no usid,
    /// which surfaces as a clear failure.</summary>
    private static async Task<(string? Usid, string? Upst, string? Error)> LoginAsync(
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postForm,
        string? email,
        string? password,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            return (null, null, "Upstore account needs an email and password.");
        }

        Dictionary<string, string> form = new(StringComparer.Ordinal)
        {
            ["url"] = HomeUrl,
            ["email"] = email,
            ["password"] = password,
            ["send"] = "Login",
        };

        HttpResponseSnapshot snap;
        try
        {
            snap = await postForm(LoginUrl, form);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, null, "Upstore login request failed: " + ex.Message);
        }

        string? usid = ExtractCookieValue(snap.SetCookies, "usid");
        return usid is not null
            ? (usid, ExtractCookieValue(snap.SetCookies, "upst"), null)
            : (null, null, $"Upstore login failed — check the email and password (HTTP {snap.StatusCode}).");
    }

    /// <summary>GETs the homepage and scrapes the rotating Dropzone upload-form action URL.</summary>
    private async Task<(string? ActionUrl, string? Error)> FetchUploadActionAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot snap;
        try
        {
            snap = await GetSnapshotAsync(ctx, HomeUrl);
        }
        catch (Exception ex)
        {
            return (null, "Upstore homepage fetch failed: " + ex.Message);
        }

        Match action = _uploadActionRegex.Match(snap.Body);
        return action.Success
            ? (action.Groups[1].Value, null)
            : (null, $"Upstore homepage did not contain an upload-form action URL (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string actionUrl, string? usid)
    {
        // Anonymous: just the file. Account: the file + the usid that links it to the account.
        Dictionary<string, string> extraFields = [with(StringComparer.Ordinal)];
        if (!string.IsNullOrEmpty(usid))
        {
            extraFields["usid"] = usid;
        }

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = HomeUrl,
            // The Dropzone client marks the upload as an XHR and asks for the JSON result shape.
            ["X-Requested-With"] = "XMLHttpRequest",
            ["Accept"] = "application/json",
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, actionUrl, extraFields, headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            actionUrl,
            fileFieldName: "file",
            extraFields: extraFields,
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    /// <summary>
    /// Success is HTTP 200 with JSON <c>{"hash":"&lt;code&gt;",…}</c>; the share link is
    /// <c>https://upstore.net/&lt;code&gt;</c>. A non-2xx, an unparseable body, or a missing hash
    /// (any error the server reports) surfaces as a failure with the body snippet.
    /// </summary>
    private static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"Upstore upload failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        UpstoreUploadResult? result;
        try
        {
            result = JsonSerializer.Deserialize<UpstoreUploadResult>(response.Body);
        }
        catch
        {
            result = null;
        }

        if (result is { Error.Length: > 0 })
        {
            return (null, $"Upstore upload was rejected: {result.Error}");
        }

        if (result is null || string.IsNullOrEmpty(result.Hash))
        {
            return (null, $"Upstore upload did not return a file hash (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        return (PublicUrlPrefix + result.Hash, null);
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
        => _postFormOverride is not null
            ? _postFormOverride(url, form)
            : handler.PostFormAsync(url, form, ct);

    private Task<HttpResponseSnapshot> GetSnapshotAsync(AttemptContext ctx, string url)
        => GetSnapshotAsync(ctx.Handler, url, headers: null, ctx.Cancellation);

    private Task<HttpResponseSnapshot> GetSnapshotAsync(HttpHandler handler, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
        => _getSnapshotOverride is not null
            ? _getSnapshotOverride(url)
            : handler.GetSnapshotAsync(url, headers, ct);

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

    private sealed class UpstoreUploadResult
    {
        [JsonPropertyName("hash")] public string? Hash { get; set; }

        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    [GeneratedRegex("""action=["']([^"']*newupload[^"']*)["']""", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
    private static partial Regex MyRegex();
}
