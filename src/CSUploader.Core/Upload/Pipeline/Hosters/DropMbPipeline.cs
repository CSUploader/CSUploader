// <copyright file="DropMbPipeline.cs" company="CSUploader">
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
/// DropMB (dropmb.com) — a <b>Pingvin Share</b> instance: anonymous or signed in, <b>512 MB</b>,
/// 10 MB chunks. Three calls, all JSON/REST:
/// <list type="number">
///   <item><b>Create the share.</b> <c>POST /api/shares</c> with
///   <c>{"id":…,"expiration":…,"recipients":[],"security":{}}</c> → 201.</item>
///   <item><b>Send the chunks.</b> <c>POST /api/shares/&lt;id&gt;/files?name=&amp;chunkIndex=&amp;totalChunks=</c>
///   with the raw slice as <c>application/octet-stream</c> → 201 <c>{"id":…,"name":…}</c>.
///   <b>⚠ Every chunk after the first must carry <c>&amp;id=</c> that file id</b> — the host does not
///   track slices by share + filename, and without it chunk 2 is refused with
///   <c>unexpected_chunk_index, expectedChunkIndex: 0</c> ("I have never seen this file"). A capture of
///   a single-chunk upload cannot show this; it took a real 3-chunk transfer to find.</item>
///   <item><b>Complete.</b> <c>POST /api/shares/&lt;id&gt;/complete</c> (empty body) → 202. The share
///   is <c>https://dropmb.com/share/&lt;id&gt;</c>.</item>
/// </list>
/// <para>
/// <b>The host publishes its own limits</b> at <c>GET /api/configs</c> (public, keyless), which is
/// where every number here comes from: <c>share.maxSize 512000000</c>, <c>share.chunkSize 10000000</c>,
/// <c>share.maxExpiration "5 years"</c>, <c>share.allowUnauthenticatedShares true</c>. Re-checking them
/// is one unauthenticated request.
/// </para>
/// <para>
/// <b>⚠ THE SHARE ID IS CLIENT-MINTED, AND IT IS THE WHOLE SECURITY MODEL.</b> Whoever knows it can
/// read the share — there is no per-file secret. The host's own default is
/// <c>share.shareIdLength 4</c>, which is guessable by anyone who cares to try; this mints <b>16</b>
/// characters from a cryptographic RNG instead. Nothing stops a caller choosing a short one, so this
/// is a deliberate improvement on the service's default rather than a copy of it.
/// </para>
/// <para>
/// <b>Retention: it asks for "never".</b> The site's own uploader sends <c>1-years</c> while the
/// instance permits up to <c>5 years</c> — and <c>never</c> is accepted too, which reads back as a
/// 1970 epoch (Pingvin's "no expiry" sentinel) and was verified by uploading, completing and then
/// fetching the share, which serves normally. A collision on the id fails loudly with
/// <c>400 "Share id already in use"</c>, so a share can never be silently overwritten and the create
/// call is its own guard — the site's <c>isShareIdAvailable</c> pre-check is not needed.
/// </para>
/// <para>
/// <b>An account is one cookie.</b> <c>POST /api/auth/signIn</c> returns
/// <c>Set-Cookie: access_token=&lt;jwt&gt;</c>, and the three upload calls carry it — no
/// <c>Authorization</c> header anywhere, which a capture of a real signed-in upload confirms. As with
/// FileMirage, an upload with the cookie missing still succeeds and simply files the share under
/// nobody, so a signed-in attempt that can't produce a token <b>fails</b> rather than quietly
/// publishing an anonymous link.
/// </para>
/// </summary>
public sealed class DropMbPipeline : IFileHosterPipeline, ISessionRefreshablePipeline
{
    private const string Host = "https://dropmb.com";
    private const string SharesUrl = Host + "/api/shares";
    private const string SignInUrl = Host + "/api/auth/signIn";
    private const string MeUrl = Host + "/api/users/me";

    /// <summary><c>share.maxSize</c> from the host's own config.</summary>
    private const long MaxFileSizeBytes = 512_000_000;

    /// <summary><c>share.chunkSize</c> from the host's own config.</summary>
    private const int ChunkSizeBytes = 10_000_000;

    /// <summary>16 characters, against the instance's own default of 4. The id IS the access control.</summary>
    private const int ShareIdLength = 16;

    private const string IdAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>The longest the instance accepts. Its own uploader asks for a single year.</summary>
    private const string Expiration = "never";

    private readonly Func<string, string?, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>>? _postJsonOverride;
    private readonly Func<string, IReadOnlyDictionary<string, string>?, long, Task<HttpResponseSnapshot>>? _chunkOverride;

    public DropMbPipeline()
    {
    }

    /// <summary>Test ctor — stubs the JSON calls and the per-chunk body POST.</summary>
    internal DropMbPipeline(
        Func<string, string?, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> postJsonOverride,
        Func<string, IReadOnlyDictionary<string, string>?, long, Task<HttpResponseSnapshot>> chunkOverride)
    {
        _postJsonOverride = postJsonOverride;
        _chunkOverride = chunkOverride;
    }

    public string Name => "DropMB";

    /// <summary>Downloads are captcha-free: the Pingvin Share download API returned the bytes
    /// after an auto-issued share token; configs expose no captcha keys (live probe,
    /// 2026-08-20).</summary>
    public DownloadCaptchaRequirement DownloadCaptcha => DownloadCaptchaRequirement.NotRequired;

    public bool RequiresHashingBeforeUpload => false;

    public bool RequiresHashingAfterUpload => false;

    public long? MaxFileSize => MaxFileSizeBytes;

    /// <summary>Permanent: the upload asks for no expiry (where the site's own uploader sends
    /// <c>1-years</c>) and the share came back stamped with Pingvin's 1970-epoch "never" sentinel,
    /// verified by uploading, completing and re-reading the share.</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials) => FileRetention.Permanent;

    public int? MaxFilesPerPackage => null;

    public bool SupportsAnonymousUpload => true;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds DropMB's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Decimal).ToFriendlyString()} per-share limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === The account, when one was chosen ===
        // Without the cookie the share is created under nobody and still returns a working link, so a
        // missing token has to stop the upload rather than quietly publish an anonymous share.
        string? token = null;
        if (!ctx.Credentials.IsAnonymous)
        {
            (token, string? authError) = await ResolveTokenAsync(ctx);
            if (token is null)
            {
                yield return new AttemptFailed(
                    authError ?? "DropMB has no sign-in for this account, and uploading without one would "
                    + "put the share under no account. Re-check the account in Account Manager.",
                    null);
                yield break;
            }
        }

        // === 1. create the share ===
        string shareId = NewShareId();
        (bool created, string? createError) = await CreateShareAsync(ctx, shareId, token);
        if (!created)
        {
            yield return new AttemptFailed(createError!, null);
            yield break;
        }

        // === 2. the chunks ===
        yield return new TransferStarted(ctx.FileSize);

        var progressChannel = Channel.CreateUnbounded<UploadEvent>();
        void OnProgress(object? _, OperationProgressEventArgs e) =>
            progressChannel.Writer.TryWrite(new TransferProgress(e.BytesProcessed, e.Size, e.Speed));
        ctx.Handler.UploadProgress += OnProgress;

        Task<string?> uploadTask = SendChunksAsync(ctx, shareId, token);
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

        if (await uploadTask is { } chunkError)
        {
            yield return new AttemptFailed(chunkError, null);
            yield break;
        }

        // === 3. complete, or the share stays a draft ===
        if (await CompleteAsync(ctx, shareId, token) is { } completeError)
        {
            yield return new AttemptFailed(completeError, null);
            yield break;
        }

        yield return new TransferCompleted($"{Host}/share/{shareId}");
    }

    /// <summary>16 random characters. The host would accept 4 — see the class remarks on why that is
    /// not good enough when the id is the only thing guarding the share.</summary>
    internal static string NewShareId()
    {
        char[] id = new char[ShareIdLength];
        for (int i = 0; i < id.Length; i++)
        {
            id[i] = IdAlphabet[RandomNumberGenerator.GetInt32(IdAlphabet.Length)];
        }

        return new string(id);
    }

    private async Task<(bool Created, string? Error)> CreateShareAsync(AttemptContext ctx, string shareId, string? token)
    {
        string json = JsonSerializer.Serialize(new
        {
            id = shareId,
            expiration = Expiration,
            recipients = Array.Empty<string>(),
            security = new { },
        });

        HttpResponseSnapshot response;
        try
        {
            response = await PostJsonAsync(ctx, SharesUrl, json, token);
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"DropMB share creation failed: {ex.Message}");
        }

        return response.StatusCode is >= 200 and < 300
            ? (true, null)
            : (false, $"DropMB wouldn't create the share (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}");
    }

    /// <summary>Sends the file in <c>share.chunkSize</c> slices. Returns null on success, or the
    /// message describing which chunk the host refused.</summary>
    private async Task<string?> SendChunksAsync(AttemptContext ctx, string shareId, string? token)
    {
        long fileSize = ctx.FileSize;
        int totalChunks = fileSize <= ChunkSizeBytes ? 1 : (int)((fileSize + ChunkSizeBytes - 1) / ChunkSizeBytes);
        DateTime started = DateTime.Now;

        await using FileStream file = new(ctx.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        long position = 0;

        // The id the FIRST chunk is given, threaded through every one after it. This is what ties the
        // slices together — the host does NOT track them by share + filename, and without it the
        // second chunk is refused with `unexpected_chunk_index, expectedChunkIndex: 0`, i.e. "I have
        // never seen this file". Nothing in a single-chunk capture can show this.
        string? fileId = null;

        for (int index = 0; index < totalChunks; index++)
        {
            long thisChunk = Math.Min(ChunkSizeBytes, fileSize - position);
            string endpoint = $"{SharesUrl}/{shareId}/files"
                + $"?name={Uri.EscapeDataString(ctx.FileName)}"
                + $"&chunkIndex={index.ToString(CultureInfo.InvariantCulture)}"
                + $"&totalChunks={totalChunks.ToString(CultureInfo.InvariantCulture)}"
                + (fileId is null ? string.Empty : $"&id={Uri.EscapeDataString(fileId)}");

            HttpResponseSnapshot response;
            if (_chunkOverride is not null)
            {
                response = await _chunkOverride(endpoint, Headers(token), thisChunk);
            }
            else
            {
                file.Position = position;
                // PutChunkAsync already sends a raw octet-stream body with progress; the only
                // difference here is the verb, which this API wants as POST.
                response = await ctx.Handler.PutChunkAsync(
                    endpoint,
                    new ChunkSliceStream(file, thisChunk),
                    thisChunk,
                    basePosition: position,
                    totalFileSize: fileSize,
                    dateTimeStarted: started,
                    ctx.SpeedBudget,
                    headers: Headers(token),
                    cancellationToken: ctx.Cancellation,
                    method: HttpMethod.Post);
            }

            if (response.StatusCode is < 200 or >= 300)
            {
                return $"DropMB rejected chunk {(index + 1).ToString(CultureInfo.InvariantCulture)}/{totalChunks.ToString(CultureInfo.InvariantCulture)} "
                    + $"(HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}";
            }

            if (fileId is null && ReadFileId(response.Body) is { } issued)
            {
                fileId = issued;
            }
            else if (fileId is null && totalChunks > 1)
            {
                // Carrying on would send chunk 2 with no id and get the same refusal, only after
                // another slice had gone up the wire.
                return "DropMB accepted the first chunk but named no file, so the rest of the upload has nothing to attach to.";
            }

            position += thisChunk;
        }

        return null;
    }

    /// <summary>Finalises the share. Until this lands the bytes are up but the share is a draft, so a
    /// failure here is a failed upload.</summary>
    private async Task<string?> CompleteAsync(AttemptContext ctx, string shareId, string? token)
    {
        try
        {
            HttpResponseSnapshot response = await PostJsonAsync(ctx, $"{SharesUrl}/{shareId}/complete", null, token);
            return response.StatusCode is >= 200 and < 300
                ? null
                : $"DropMB took the file but wouldn't publish the share (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}): {Snippet(response.Body)}";
        }
        catch (OperationCanceledException) when (ctx.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"DropMB took the file but the share couldn't be published: {ex.Message}";
        }
    }

    /// <summary>The account's token: the stored one when it's still there, else a fresh sign-in with
    /// the saved username and password.</summary>
    private async Task<(string? Token, string? Error)> ResolveTokenAsync(AttemptContext ctx)
    {
        if (NullIfWhiteSpace(ctx.Credentials.SessionCookie) is { } stored)
        {
            return (stored, null);
        }

        (string? token, string? error) = await SignInAsync(
            ctx.Handler,
            ctx.Credentials.Username,
            ctx.Credentials.Password,
            ctx.Cancellation,
            (url, json, headers) => _postJsonOverride is not null
                ? _postJsonOverride(url, json, headers)
                : ctx.Handler.PostJsonAsync(url, json, headers, ctx.Cancellation));

        return (token, error);
    }

    /// <summary>
    /// Posts the sign-in and pulls <c>access_token</c> out of the reply. The cookie is the credential:
    /// the body's <c>accessToken</c> carries the same JWT, but every call the site makes authenticates
    /// with the cookie and none of them sends an <c>Authorization</c> header.
    /// </summary>
    private static async Task<(string? Token, string? Error)> SignInAsync(
        HttpHandler handler,
        string? username,
        string? password,
        CancellationToken ct,
        Func<string, string?, IReadOnlyDictionary<string, string>?, Task<HttpResponseSnapshot>> post)
    {
        _ = handler;
        _ = ct;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return (null, "DropMB needs the account's username and password.");
        }

        string json = JsonSerializer.Serialize(new { username, password });

        HttpResponseSnapshot response;
        try
        {
            response = await post(SignInUrl, json, Headers(null));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, "DropMB sign-in failed: " + ex.Message);
        }

        if (response.StatusCode is < 200 or >= 300)
        {
            return (null, $"DropMB rejected the sign-in — check the username and password (HTTP {response.StatusCode.ToString(CultureInfo.InvariantCulture)}).");
        }

        return ReadAccessToken(response) is { } token
            ? (token, null)
            : (null, "DropMB signed in but issued no access token, so the share would be filed under no account.");
    }

    /// <summary>Reads the <c>id</c> a chunk reply carries — the handle that ties the remaining chunks
    /// to this file. Internal for testing.</summary>
    internal static string? ReadFileId(string body)
    {
        try
        {
            JsonElement root = JsonDocument.Parse(body).RootElement;
            return root.TryGetProperty("id", out JsonElement id) ? NullIfWhiteSpace(id.GetString()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads <c>access_token</c> from the sign-in reply — the Set-Cookie first, then the
    /// body's <c>accessToken</c>. Internal for testing.</summary>
    internal static string? ReadAccessToken(HttpResponseSnapshot response)
    {
        foreach (string cookie in response.SetCookies)
        {
            if (cookie.StartsWith("access_token=", StringComparison.OrdinalIgnoreCase))
            {
                return NullIfWhiteSpace(cookie.Split(';', 2)[0]["access_token=".Length..]);
            }
        }

        try
        {
            JsonElement root = JsonDocument.Parse(response.Body).RootElement;
            return root.TryGetProperty("accessToken", out JsonElement t) ? NullIfWhiteSpace(t.GetString()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task<HttpResponseSnapshot> PostJsonAsync(AttemptContext ctx, string url, string? json, string? token)
        => _postJsonOverride is not null
            ? _postJsonOverride(url, json, Headers(token))
            : ctx.Handler.PostJsonAsync(url, json, Headers(token), ctx.Cancellation);

    /// <summary>Request headers. <paramref name="token"/> rides as the <c>access_token</c> cookie,
    /// which is how the site itself authenticates every one of these calls.</summary>
    private static Dictionary<string, string> Headers(string? token)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["Origin"] = Host,
            ["Referer"] = Host + "/upload",
            ["Accept"] = "application/json, text/plain, */*",
        };

        if (token is not null)
        {
            headers["Cookie"] = "access_token=" + token;
        }

        return headers;
    }

    /// <summary>
    /// Signs in and keeps the token. The username is returned exactly as typed: it is the identifier
    /// the next sign-in posts, so nothing scraped may replace it.
    /// </summary>
    public async Task<AccountCheckResult> CheckAccountAsync(string username, string password, string? apiKey, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        (string? token, string? error) = await SignInAsync(
            handler, username, password, ct,
            (url, json, headers) => _postJsonOverride is not null
                ? _postJsonOverride(url, json, headers)
                : handler.PostJsonAsync(url, json, headers, ct));

        if (token is null)
        {
            return new AccountCheckResult(false, AccountType.Free, error);
        }

        return new AccountCheckResult(
            true,
            AccountType.Free,
            "Signed in to DropMB.",
            SessionCookie: token,

            // general.sessionDuration on this instance is a year; a short window here would only mean
            // needless re-signing, and an expired token is caught by the refresh below either way.
            SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(30),
            DerivedUsername: username);
    }

    /// <summary>
    /// Re-checks the stored token without a password, by asking who it belongs to. A dead token
    /// answers with no user, which is the only signal this API gives.
    /// </summary>
    public async Task<AccountCheckResult> RefreshAccountAsync(string? apiKey, string sessionCookie, HttpHandler handler, Lib.Net.ProxyChoice proxy, CancellationToken ct)
    {
        _ = apiKey;
        _ = proxy;

        try
        {
            HttpResponseSnapshot me = await handler.GetSnapshotAsync(MeUrl, Headers(sessionCookie), ct);
            if (me.StatusCode is >= 200 and < 300 && me.Body.Contains("\"username\"", StringComparison.Ordinal))
            {
                return new AccountCheckResult(
                    true,
                    AccountType.Free,
                    "Signed in to DropMB.",
                    SessionCookie: sessionCookie,
                    SessionCookieExpiresUtc: DateTime.UtcNow.AddDays(30));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Fall through to the same "sign in again" answer a rejected token gets.
        }

        return new AccountCheckResult(false, AccountType.Free, "The saved DropMB sign-in is no longer valid — sign in again.");
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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
}
