// <copyright file="GofilePipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// gofile.io upload pipeline — anonymous (guest account), no login. Mirrors the flow gofile's own
/// site JS performs (each step's wire shape reconciled against a live capture + the site bundle):
/// <list type="number">
///   <item><c>POST https://api.gofile.io/accounts</c> (no body) → a guest account whose
///   <c>token</c> and <c>rootFolder</c> come back together.</item>
///   <item><c>POST https://api.gofile.io/contents/createfolder</c> (<c>Bearer token</c>,
///   <c>{parentFolderId: rootFolder, public: true}</c>) → a fresh public folder (its <c>id</c> is the
///   upload target, its <c>code</c> is the share slug).</item>
///   <item><c>POST https://upload.gofile.io/uploadfile</c> (multipart <c>token</c> + <c>folderId</c> +
///   <c>file</c>) → the file; the share link is the response's <c>downloadPage</c>
///   (<c>https://gofile.io/d/&lt;code&gt;</c>).</item>
/// </list>
/// The first two steps create no file, so a mid-send upload fault is safe to retry (a fresh guest
/// account + folder). No hashing, no account, no size cap (gofile enforces its own). Verified
/// end-to-end against the live API.
/// </summary>
public sealed class GofilePipeline : IFileHosterPipeline
{
    private const string ApiBase = "https://api.gofile.io";
    private const string UploadUrl = "https://upload.gofile.io/uploadfile";
    private const string Origin = "https://gofile.io";

    private readonly Func<HttpMethod, string, string?, string?, Task<HttpResponseSnapshot>>? _apiOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;
    private readonly Func<int, CancellationToken, Task> _retryDelay;

    // ONE guest account is created and reused across every anonymous upload (matching gofile's site,
    // which caches a single guest account per browser). Creating one PER FILE would trip gofile's
    // per-IP account-creation rate limit (it 502s after a few). Gated so a burst of files does one
    // account creation, not N; invalidated if the token ever stops working.
    private readonly SemaphoreSlim _guestGate = new(1, 1);
    private (string Token, string RootFolder)? _guest;

    public GofilePipeline()
    {
        _retryDelay = static (attempt, ct) => Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
    }

    /// <summary>Test ctor — stubs the JSON API calls (accounts / createfolder) and the multipart upload
    /// so the orchestration runs without the network, and zeroes the retry backoff. The <c>api</c> stub
    /// receives (method, url, jsonBody, bearerToken).</summary>
    internal GofilePipeline(
        Func<HttpMethod, string, string?, string?, HttpResponseSnapshot> api,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, HttpResponseSnapshot> upload)
    {
        _apiOverride = (m, u, j, t) => Task.FromResult(api(m, u, j, t));
        _uploadOverride = (fp, u, f, h, s) => Task.FromResult(upload(fp, u, f, h, s));
        _retryDelay = static (_, _) => Task.CompletedTask;
    }

    public string Name => "Gofile";

    /// <summary>Downloads are captcha-free: a live guest session fetched the bytes from the
    /// download page with zero captcha widgets in the DOM (probe, 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    /// <summary>From its own FAQ (read 2026-08-12): content is kept for 10 days and stays longer
    /// only while it keeps being downloaded - matching the host's own public statement that files
    /// "remain on the servers for at least 10 days" and go when not downloaded. Uploads here ride a
    /// guest account, which is exactly the tier that policy describes.</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials)
        => FileRetention.DaysAfterLastDownload(10);

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => null; // gofile enforces its own guest limit server-side.

    public int? MaxFilesPerPackage => null;

    /// <summary>gofile.io needs no account — each upload spins up its own anonymous guest account, so
    /// the wizard offers it as the built-in "Anonymous" option.</summary>
    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // === Phase 1: guest account (token + rootFolder, cached+reused) → a fresh public folder ===
        string? token = null;
        string? folderId = null;
        string? setupError = null;
        try
        {
            // One stale-account retry: gofile purges inactive guest accounts server-side, which surfaces as
            // createfolder rejecting the CACHED account (StaleGuestAccountException — the cache is already
            // dropped when it's thrown). The second pass mints a fresh account. A rejection of a FRESH
            // account (second throw) falls through to the generic catch → a clear AttemptFailed.
            for (int setupAttempt = 0; ; setupAttempt++)
            {
                try
                {
                    (string t, string rootFolder) = await EnsureGuestAsync(ctx);
                    token = t;
                    folderId = await CreateFolderAsync(ctx, token, rootFolder);
                    break;
                }
                catch (StaleGuestAccountException) when (setupAttempt == 0)
                {
                    // loop once — the cache is empty now, so EnsureGuestAsync creates a fresh account
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            setupError = "gofile.io setup failed: " + ex.Message;
        }

        if (setupError is not null)
        {
            yield return new AttemptFailed(setupError, null);
            yield break;
        }

        // === Phase 2: upload the file into the folder ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<HttpResponseSnapshot> uploadTask = UploadWithRetryAsync(ctx, token!, folderId!);
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

        // A mid-send transport fault (UploadMultipartAsync reclassified it) propagates to the shared
        // retry layer — the file never landed, so re-running against a fresh account/folder is safe.
        HttpResponseSnapshot uploadResponse = await uploadTask;

        (string? url, string? error) = ParseUploadResponse(uploadResponse);
        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    /// <summary>gofile.io has no account sign-in in this app — uploads use the built-in Anonymous option.</summary>
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
            "gofile.io has no account sign-in — upload with the built-in Anonymous option in the wizard."));
    }

    // ------------------------------------------------------------------ phase-1 API steps

    /// <summary>The cached guest account (token + rootFolder), created once on first use and reused for
    /// every subsequent upload. Gated so a burst of files does a single account creation, not one each
    /// (which would trip gofile's per-IP account-creation rate limit).</summary>
    private async Task<(string Token, string RootFolder)> EnsureGuestAsync(AttemptContext ctx)
    {
        if (_guest is { } cached)
        {
            return cached;
        }

        await _guestGate.WaitAsync(ctx.Cancellation).ConfigureAwait(false);
        try
        {
            if (_guest is { } c)
            {
                return c;
            }

            (string Token, string RootFolder) guest = await CreateGuestAccountAsync(ctx);
            _guest = guest;
            return guest;
        }
        finally
        {
            _guestGate.Release();
        }
    }

    /// <summary>POST /accounts (no body) → a guest account; returns its token + rootFolder (both in the
    /// same response).</summary>
    private async Task<(string Token, string RootFolder)> CreateGuestAccountAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot snap = await ApiWithRetryAsync(ctx, HttpMethod.Post, ApiBase + "/accounts", json: null, bearer: null);
        return (RequireDataString(snap, "token", "accounts"), RequireDataString(snap, "rootFolder", "accounts"));
    }

    /// <summary>POST /contents/createfolder (Bearer) → a fresh public folder; returns its id. A rejection of
    /// the CACHED guest account — HTTP 401/403, or gofile's HTTP-200 + <c>error-notFound</c>/<c>error-auth</c>
    /// envelope (guest accounts are purged server-side after inactivity, taking their rootFolder with them) —
    /// drops the cache and throws <see cref="StaleGuestAccountException"/> so the setup loop retries once
    /// against a freshly minted account.</summary>
    private async Task<string> CreateFolderAsync(AttemptContext ctx, string token, string rootFolder)
    {
        string body = JsonSerializer.Serialize(new { parentFolderId = rootFolder, @public = true });
        HttpResponseSnapshot snap = await ApiWithRetryAsync(ctx, HttpMethod.Post, ApiBase + "/contents/createfolder", body, bearer: token);
        if (snap.StatusCode is 401 or 403 || BodyStatus(snap) is "error-notFound" or "error-auth")
        {
            _guest = null;
            throw new StaleGuestAccountException(
                $"createfolder rejected the guest account (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
        }

        return RequireDataString(snap, "id", "createfolder");
    }

    /// <summary>The gofile envelope's <c>status</c> string, or null when the body isn't that shape.</summary>
    private static string? BodyStatus(HttpResponseSnapshot snap)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(snap.Body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("status", out JsonElement s)
                   && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The cached guest account was rejected server-side (purged/expired). The cache has already
    /// been dropped; the setup loop catches this once and re-runs against a fresh account.</summary>
    private sealed class StaleGuestAccountException(string message) : InvalidOperationException(message);

    /// <summary>Issues an API call, retrying on a transient gateway failure (429 or 5xx) with backoff —
    /// gofile's own client classifies those as retryable (its guest API 502s under load). A success or a
    /// non-transient 4xx returns immediately.</summary>
    private async Task<HttpResponseSnapshot> ApiWithRetryAsync(AttemptContext ctx, HttpMethod method, string url, string? json, string? bearer)
    {
        HttpResponseSnapshot snap = await ApiAsync(ctx, method, url, json, bearer);
        for (int attempt = 0; IsTransient(snap.StatusCode) && attempt < 3; attempt++)
        {
            await _retryDelay(attempt, ctx.Cancellation).ConfigureAwait(false);
            snap = await ApiAsync(ctx, method, url, json, bearer);
        }

        return snap;
    }

    private static bool IsTransient(int status) => status == 429 || status is >= 500 and < 600;

    /// <summary>Runs the upload POST, retrying a transient gateway verdict (429/5xx) with the same bounded
    /// backoff as the API steps — gofile's edge intermittently 502s "Error forwarding request to upload
    /// server" even when the platform is otherwise healthy. Re-sending is safe: a 5xx means no confirmed
    /// file record, and the worst ambiguous case only duplicates the file inside this upload's OWN fresh
    /// folder (same downloadPage either way). Only status verdicts retry here — a mid-send transport fault
    /// still throws and propagates to the shared retry layer, which re-runs the whole pipeline.</summary>
    private async Task<HttpResponseSnapshot> UploadWithRetryAsync(AttemptContext ctx, string token, string folderId)
    {
        HttpResponseSnapshot snap = await UploadAsync(ctx, token, folderId);
        for (int attempt = 0; IsTransient(snap.StatusCode) && attempt < 3; attempt++)
        {
            await _retryDelay(attempt, ctx.Cancellation).ConfigureAwait(false);
            snap = await UploadAsync(ctx, token, folderId);
        }

        return snap;
    }

    private async Task<HttpResponseSnapshot> UploadAsync(AttemptContext ctx, string token, string folderId)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal)
        {
            ["token"] = token,
            ["folderId"] = folderId,
        };
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Origin,
            ["Referer"] = Origin + "/",
            ["Accept"] = "application/json",
        };

        if (_uploadOverride is not null)
        {
            return await _uploadOverride(ctx.FilePath, UploadUrl, fields, headers, ctx.SpeedLimitProvider);
        }

        return await ctx.Handler.UploadMultipartAsync(
            ctx.FilePath,
            UploadUrl,
            fileFieldName: "file",
            extraFields: fields,
            headers: headers,
            getBytesPerSecond: ctx.SpeedLimitProvider,
            cancellationToken: ctx.Cancellation);
    }

    private async Task<HttpResponseSnapshot> ApiAsync(AttemptContext ctx, HttpMethod method, string url, string? json, string? bearer)
    {
        if (_apiOverride is not null)
        {
            return await _apiOverride(method, url, json, bearer);
        }

        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Accept"] = "application/json",
            ["Origin"] = Origin,
            ["Referer"] = Origin + "/",
        };
        if (bearer is not null)
        {
            headers["Authorization"] = "Bearer " + bearer;
        }

        return method == HttpMethod.Get
            ? await ctx.Handler.GetSnapshotAsync(url, headers, ctx.Cancellation)
            : await ctx.Handler.SendJsonAsync(method, url, json, headers, ctx.Cancellation);
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>Pulls <c>data.&lt;field&gt;</c> from a <c>{status:"ok", data:{…}}</c> gofile envelope,
    /// throwing a clear error when the HTTP status is bad, the envelope isn't ok, or the field is
    /// missing/empty.</summary>
    private static string RequireDataString(HttpResponseSnapshot snap, string field, string step)
    {
        if (snap.StatusCode is < 200 or >= 300)
        {
            throw new InvalidOperationException($"{step} failed (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(snap.Body);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("status", out JsonElement status) && status.GetString() != "ok")
            {
                throw new InvalidOperationException($"{step} returned status '{status.GetString()}': {Snippet(snap.Body)}");
            }

            if (root.TryGetProperty("data", out JsonElement data)
                && data.TryGetProperty(field, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(value.GetString()))
            {
                return value.GetString()!;
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{step} returned an unparseable body: {Snippet(snap.Body)}");
        }

        throw new InvalidOperationException($"{step} returned no '{field}': {Snippet(snap.Body)}");
    }

    /// <summary>Success is HTTP 200 with <c>{status:"ok", data:{downloadPage:"https://gofile.io/d/…"}}</c>.</summary>
    private static (string? Url, string? Error) ParseUploadResponse(HttpResponseSnapshot response)
    {
        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"gofile.io upload failed (HTTP {response.StatusCode}): {Snippet(response.Body)}");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("status", out JsonElement status) && status.GetString() != "ok")
            {
                return (null, $"gofile.io upload was rejected (status '{status.GetString()}'): {Snippet(response.Body)}");
            }

            if (root.TryGetProperty("data", out JsonElement data)
                && data.TryGetProperty("downloadPage", out JsonElement page)
                && page.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(page.GetString()))
            {
                return (page.GetString(), null);
            }
        }
        catch (JsonException)
        {
            return (null, $"gofile.io upload returned an unparseable body: {Snippet(response.Body)}");
        }

        return (null, $"gofile.io upload returned no downloadPage: {Snippet(response.Body)}");
    }

    private static string Snippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string trimmed = body.Trim().Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        const int Max = 200;
        return trimmed.Length > Max ? trimmed[..Max] + "…" : trimmed;
    }
}
