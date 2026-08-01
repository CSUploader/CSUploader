// <copyright file="FilestankPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// Filestank (filestank.com) — account upload over the site's own documented REST API. It is NOT
/// XFileSharing: it runs <b>YetiShare</b> (<c>/api/v2/</c>, <c>themes/spirit</c>), a commercial
/// file-sharing script used by a number of hosts, so this pipeline is shaped to be worth copying if a
/// second YetiShare host is ever added.
/// <list type="number">
///   <item><b>Authorise.</b> <c>POST /api/v2/authorize</c> with <c>key1</c> + <c>key2</c> (two
///   64-character account keys) → <c>{"data":{"access_token":…,"account_id":…}}</c>.</item>
///   <item><b>Upload.</b> <c>POST /api/v2/file/upload</c> — multipart <c>upload_file</c> plus the
///   <c>access_token</c> and <c>account_id</c> → <c>{"response":"File uploaded","data":[{…,"url":…}]}</c>.
///   The share link is that <c>url</c>.</item>
/// </list>
/// <para>
/// <b>Credentials are the two API keys, entered as username + password</b> — key1 in the username box,
/// key2 in the password box. That mapping is deliberate rather than elegant: the account dialog's
/// API-key mode offers a single paste box plus a "Sign in" button, and neither fits a host that needs
/// TWO keys and has no sign-in that could produce them. Two plain fields for two keys at least can't
/// mislead. The keys come from the account's own API page on filestank.com.
/// </para>
/// <para>
/// <b>The access token is cached per account</b>, because the API's own documentation says so: "the
/// same access_token can be used multiple times in the same session, so you shouldn't generate a new
/// access_token for each request." A batch of 80 files therefore costs one authorise, not eighty. A
/// rejected upload drops the cache once and re-authorises, so an expired token self-heals.
/// </para>
/// <para>
/// <b>No per-file cap is declared</b> — the site publishes no figure and the API exposes none, so
/// <see cref="MaxFileSize"/> stays null and the server's own refusal is the authority. (A candidate
/// note claimed 20 GB; it is unverified, and encoding a guess would reject files the server would
/// have taken.)
/// </para>
/// </summary>
public sealed class FilestankPipeline : IFileHosterPipeline
{
    private const string ApiBase = "https://www.filestank.com/api/v2/";
    private const string AuthorizeUrl = ApiBase + "authorize";
    private const string UploadUrl = ApiBase + "file/upload";

    /// <summary>Authorised sessions by credentials id. See the class remarks — the API asks callers to
    /// reuse the token rather than re-authorise per request.</summary>
    private readonly ConcurrentDictionary<int, (string AccessToken, string AccountId)> _sessions = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _authGates = new();

    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>>? _postFormOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public FilestankPipeline()
    {
    }

    /// <summary>Test ctor — drives the authorise call and the upload from canned responses.</summary>
    internal FilestankPipeline(
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpResponseSnapshot>> postFormOverride,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>> uploadOverride)
    {
        _postFormOverride = postFormOverride;
        _uploadOverride = uploadOverride;
    }

    public string Name => "Filestank";

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    /// <summary>No cap is published by the site or the API; the server decides.</summary>
    public long? MaxFileSize => null;

    public int? MaxFilesPerPackage => null;

    /// <summary>Account-only — every API call needs an authorised token.</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        (string? key1, string? key2) = ReadKeys(ctx.Credentials);
        if (key1 is null || key2 is null)
        {
            yield return new AttemptFailed(
                "Filestank needs both API keys — open Settings → Accounts and enter API key 1 as the username and API key 2 as the password.",
                null);
            yield break;
        }

        (string? token, string? accountId, string? authError) = await EnsureSessionAsync(ctx, key1, key2, forceRefresh: false);
        if (token is null)
        {
            yield return new AttemptFailed(authError!, null);
            yield break;
        }

        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void onProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += onProgress;

        Task<(string? Url, string? Error, bool Unauthorised)> workTask = UploadOnceAsync(ctx, token, accountId!);

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

        (string? url, string? error, bool unauthorised) = await workTask;

        // A cached token that has expired shows up as an auth refusal on the upload. Re-authorise ONCE
        // and re-send; anything else is a real verdict and is reported as-is. The retry re-sends the
        // whole file, so it is capped at one and gated on an auth failure specifically.
        if (unauthorised)
        {
            ctx.Logger.Log(this, LogType.Status, $"{Name}: access token rejected; re-authorising and retrying once.");
            (string? freshToken, string? freshAccountId, string? refreshError) = await EnsureSessionAsync(ctx, key1, key2, forceRefresh: true);
            if (freshToken is null)
            {
                yield return new AttemptFailed(refreshError!, null);
                yield break;
            }

            (url, error, _) = await UploadOnceAsync(ctx, freshToken, freshAccountId!);
        }

        if (error is not null)
        {
            yield return new AttemptFailed(error, null);
            yield break;
        }

        yield return new TransferCompleted(url!);
    }

    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        string? key1 = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        string? key2 = string.IsNullOrWhiteSpace(password) ? null : password.Trim();
        if (key1 is null || key2 is null)
        {
            return new AccountCheckResult(
                false,
                AccountType.Free,
                "Filestank needs both API keys: enter API key 1 as the username and API key 2 as the password (find them on your Filestank account's API page).");
        }

        HttpResponseSnapshot snap;
        try
        {
            snap = _postFormOverride is not null
                ? await _postFormOverride(AuthorizeUrl, BuildAuthForm(key1, key2))
                : await handler.PostFormAsync(AuthorizeUrl, BuildAuthForm(key1, key2), headers: null, ct);
        }
        catch (Exception ex)
        {
            return new AccountCheckResult(false, AccountType.Free, "Filestank authorise request failed: " + ex.Message);
        }

        (string? token, string? accountId, string? error) = ParseAuthorizeResponse(snap.Body);

        // DELIBERATELY no DerivedUsername, even though the response hands us an account id. The
        // Settings VM copies DerivedUsername straight onto the DTO's Username — safe for every other
        // hoster, because theirs is a display name, but here Username IS key1. Returning anything
        // would overwrite half the credential and break the account on its first verify. The id goes
        // in the status text instead.
        return token is null
            ? new AccountCheckResult(false, AccountType.Free, error ?? "Filestank rejected the key pair.")
            : new AccountCheckResult(true, AccountType.Free, $"Filestank keys accepted (account {accountId}).");
    }

    /// <summary>key1 = username, key2 = password. See the class remarks for why.</summary>
    private static (string? Key1, string? Key2) ReadKeys(FileHosterLoginDto credentials)
        => (string.IsNullOrWhiteSpace(credentials.Username) ? null : credentials.Username!.Trim(),
            string.IsNullOrWhiteSpace(credentials.Password) ? null : credentials.Password!.Trim());

    private static Dictionary<string, string> BuildAuthForm(string key1, string key2) => new(StringComparer.Ordinal)
    {
        ["key1"] = key1,
        ["key2"] = key2,
    };

    /// <summary>
    /// Returns a usable (token, accountId), authorising only when there isn't one cached — or when
    /// <paramref name="forceRefresh"/> says the cached one was just rejected. Serialised per account so
    /// a batch starting 20 files at once produces ONE authorise, not 20.
    /// </summary>
    private async Task<(string? Token, string? AccountId, string? Error)> EnsureSessionAsync(
        AttemptContext ctx, string key1, string key2, bool forceRefresh)
    {
        if (!forceRefresh && _sessions.TryGetValue(ctx.Credentials.Id, out (string AccessToken, string AccountId) cached))
        {
            return (cached.AccessToken, cached.AccountId, null);
        }

        SemaphoreSlim gate = _authGates.GetOrAdd(ctx.Credentials.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ctx.Cancellation).ConfigureAwait(false);
        try
        {
            // Re-check: a sibling attempt may have authorised while this one queued. On a forced
            // refresh the FIRST caller through the gate drops the stale entry, so the others behind it
            // pick up the fresh token instead of each re-authorising.
            if (_sessions.TryGetValue(ctx.Credentials.Id, out cached))
            {
                if (!forceRefresh)
                {
                    return (cached.AccessToken, cached.AccountId, null);
                }

                _sessions.TryRemove(ctx.Credentials.Id, out _);
            }

            HttpResponseSnapshot snap;
            try
            {
                snap = _postFormOverride is not null
                    ? await _postFormOverride(AuthorizeUrl, BuildAuthForm(key1, key2))
                    : await ctx.Handler.PostFormAsync(AuthorizeUrl, BuildAuthForm(key1, key2), headers: null, ctx.Cancellation);
            }
            catch (Exception ex)
            {
                return (null, null, $"{Name}: authorise request failed: {ex.Message}");
            }

            (string? token, string? accountId, string? error) = ParseAuthorizeResponse(snap.Body);
            if (token is null || accountId is null)
            {
                return (null, null, error ?? $"{Name}: authorise returned no access token (HTTP {snap.StatusCode}): {Snippet(snap.Body)}");
            }

            _sessions[ctx.Credentials.Id] = (token, accountId);
            return (token, accountId, null);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads <c>{"data":{"access_token":…,"account_id":…}}</c>, or the API's own error text out of
    /// <c>{"_status":"error","response":"…"}</c> — its refusals are legible ("The key pair may be
    /// invalid…") and worth surfacing verbatim. <c>account_id</c> arrives as a string in the docs'
    /// sample but is treated leniently. Internal for testing.
    /// </summary>
    internal static (string? Token, string? AccountId, string? Error) ParseAuthorizeResponse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null);
            }

            if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object)
            {
                string? token = ReadString(data, "access_token");
                string? accountId = ReadLoose(data, "account_id");
                if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(accountId))
                {
                    return (token, accountId, null);
                }
            }

            string? message = ReadString(root, "response");
            return (null, null, string.IsNullOrWhiteSpace(message) ? null : "Filestank: " + message);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private async Task<(string? Url, string? Error, bool Unauthorised)> UploadOnceAsync(AttemptContext ctx, string token, string accountId)
    {
        Dictionary<string, string> extraFields = new(StringComparer.Ordinal)
        {
            ["access_token"] = token,
            ["account_id"] = accountId,
        };

        HttpResponseSnapshot response = _uploadOverride is not null
            ? await _uploadOverride(ctx.FilePath, UploadUrl, extraFields, null, ctx.SpeedLimitProvider)
            : await ctx.Handler.UploadMultipartAsync(
                ctx.FilePath,
                UploadUrl,
                fileFieldName: "upload_file",
                extraFields: extraFields,
                headers: null,
                getBytesPerSecond: ctx.SpeedLimitProvider,
                cancellationToken: ctx.Cancellation);

        return ParseUploadResponse(response);
    }

    /// <summary>
    /// Success is <c>{"response":"File uploaded","data":[{…,"url":…}]}</c>. Two failure shapes matter:
    /// the envelope-level <c>{"_status":"error","response":…}</c> (which is also how an expired token
    /// arrives, hence the Unauthorised flag), and a PER-FILE <c>error</c> inside an otherwise
    /// successful-looking 200 — a shape that has bitten this project before, so it is checked before
    /// the url. Internal for testing.
    /// </summary>
    internal static (string? Url, string? Error, bool Unauthorised) ParseUploadResponse(HttpResponseSnapshot response)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.Array
                && data.GetArrayLength() > 0
                && data[0].ValueKind == JsonValueKind.Object)
            {
                JsonElement first = data[0];

                // A per-file error rides inside the 200 — report it rather than the missing url.
                if (ReadString(first, "error") is { Length: > 0 } fileError)
                {
                    return (null, $"Filestank refused the file: {fileError}", false);
                }

                if (ReadString(first, "url") is { Length: > 0 } url)
                {
                    return (url, null, false);
                }
            }

            string? message = root.ValueKind == JsonValueKind.Object ? ReadString(root, "response") : null;
            bool unauthorised = message is not null
                && (message.Contains("access_token", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("authenticate", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("authoris", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("authoriz", StringComparison.OrdinalIgnoreCase));

            return (null,
                    $"Filestank upload failed: {message ?? Snippet(response.Body)} (HTTP {response.StatusCode})",
                    unauthorised);
        }
        catch (JsonException)
        {
            return (null, $"Filestank upload returned an unreadable response (HTTP {response.StatusCode}): {Snippet(response.Body)}", false);
        }
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>Reads a value the API may send as a string OR a number (account_id is documented as a
    /// string but arrives from a numeric column).</summary>
    private static string? ReadLoose(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out JsonElement el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            _ => null,
        };
    }

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
