// <copyright file="DropMbPipeline.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters;

/// <summary>
/// DropMB (dropmb.com) — a <b>Pingvin Share</b> instance: <b>account-only</b> (it took anonymous
/// uploads when this shipped; see below), <b>512 MB</b>, 10 MB chunks. Three calls, all JSON/REST:
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
/// <b>⚠ ANONYMOUS UPLOAD IS OFF — the host switched it off after this shipped.</b> Checked 2026-10-24:
/// <c>GET /api/configs</c> reports <c>security.allowUnauthenticatedShares false</c> (and
/// <c>security.allowRegistration false</c>), an anonymous <c>POST /api/shares</c> answers
/// <c>403 {"message":"Forbidden resource","error":"Forbidden","statusCode":403}</c> — word for word what
/// the users' logs showed — and the host's own imprint now reads "Uploading a regular share requires a
/// DropMB account." That 403 is Pingvin's create-share guard, which with no valid <c>access_token</c>
/// simply returns that setting, so it says nothing about the file, the share id or the headers and no
/// retry can change it. <see cref="SupportsAnonymousUpload"/> is therefore false, and an attempt that
/// still carries the old Anonymous choice (a job queued before the switch) fails at once with a message
/// that says why, rather than sending a request that can only be refused.
/// </para>
/// <para>
/// <b>The host publishes its own limits</b> at <c>GET /api/configs</c> (public, keyless), which is
/// where every number here comes from: <c>share.maxSize 512000000</c>, <c>share.chunkSize 10000000</c>,
/// <c>share.shareIdLength 4</c>. Re-checking them is one unauthenticated request. (In August the same
/// reply also carried <c>share.maxExpiration "5 years"</c> and <c>share.allowUnauthenticatedShares true</c>;
/// the first is gone and the second is now <c>security.allowUnauthenticatedShares false</c>.)
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
/// <b>⚠ Retention: the host now states 100 days, and <see cref="RetentionFor"/> reports that.</b> Its imprint
/// (2026-10-24) says "Regular share links expire after 100 days without a visit. Each visit renews the
/// 100-day window" and "Expiration is automatic. Custom expiration dates are not offered", and
/// <c>share.maxExpiration</c> is no longer in <c>/api/configs</c>. The "never" above was measured in August
/// on the old build and this still sends it, but whether a signed-in share is still stamped with the 1970
/// sentinel could not be settled (no account to upload with; sign-up is closed). Where the two disagree the
/// published policy wins, because overstating how long someone's files live is the costly mistake.
/// </para>
/// <para>
/// <b>An account is one cookie.</b> <c>POST /api/auth/signIn</c> returns
/// <c>Set-Cookie: access_token=&lt;jwt&gt;</c>, and the three upload calls carry it — no
/// <c>Authorization</c> header anywhere, which a capture of a real signed-in upload confirms. An attempt
/// that can't produce a token <b>fails</b> before any request is made: sent without one it is refused
/// with the same bare 403 as an anonymous upload, which says nothing about why.
/// </para>
/// <para>
/// <b>⚠ That cookie is a 15-MINUTE JWT.</b> The host's <c>createAccessToken</c> signs it with
/// <c>expiresIn: "15min"</c>, and its own front end calls <c>/api/auth/token</c> for a new one whenever fewer
/// than two minutes remain — <c>security.sessionDuration</c> governs the refresh token, which this app does
/// not keep. So the token stored on the account is dead within a quarter of an hour, and a dead one is
/// refused <b>exactly like no token at all</b> (the guard falls back to <c>allowUnauthenticatedShares</c>),
/// which is how this same 403 reaches a signed-in user. While that setting was true an expired token was
/// quietly taken as an anonymous upload, filed under nobody, and that hid the problem; now it is a hard
/// failure. The stored token is therefore only a first guess: a 401 or 403 on any call signs in again with
/// the saved username and password — once, then repeats that call. The fresh token is cached per account so
/// a batch signs in once rather than per file (sign-in is throttled to 20 per 5 minutes), and it is picked
/// up part-way through too, since a large file outlasts 15 minutes. This class is deliberately <b>not</b> an
/// <see cref="ISessionRefreshablePipeline"/>: that re-check is handed no password, so it could only report
/// a perfectly good account as dead once its token lapsed — a refresh runs the sign-in instead. (Derived
/// from the host's source and its served front end; not exercised against a live account, sign-up being
/// closed.)
/// </para>
/// </summary>
public sealed class DropMbPipeline : IFileHosterPipeline
{
    private const string Host = "https://dropmb.com";
    private const string SharesUrl = Host + "/api/shares";
    private const string SignInUrl = Host + "/api/auth/signIn";

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

    // The token an earlier attempt signed in for, per saved account — see the class remarks: the one stored
    // on the account is a 15-minute JWT, so by the time a queued file runs it is nearly always dead. This is
    // what stops every file in a batch signing in again.
    private readonly ConcurrentDictionary<int, string> _freshTokens = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _signInGates = new();

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

    /// <summary>100 days counted from the last VISIT, which is what the host's imprint states (2026-10-24):
    /// "Regular share links expire after 100 days without a visit. Each visit renews the 100-day window."
    /// It used to read Permanent — the upload asks for no expiry, and in August the share came back stamped
    /// with Pingvin's 1970-epoch "never" sentinel — but that was measured on the build the host ran then and
    /// its own current text contradicts it; see the class remarks. (A visit, not strictly a download: the
    /// wizard's "after last download" label is the nearest it has.)</summary>
    public FileRetention RetentionFor(Dal.FileHosterLoginDto credentials) => FileRetention.DaysAfterLastDownload(100);

    public int? MaxFilesPerPackage => null;

    /// <summary>False since the host switched anonymous upload off (checked 2026-10-24): an anonymous
    /// <c>POST /api/shares</c> answers <c>403 Forbidden resource</c>. See the class remarks.</summary>
    public bool SupportsAnonymousUpload => false;

    public async IAsyncEnumerable<UploadEvent> RunAsync(AttemptContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = ct;

        // The host closed anonymous upload (see the class remarks). A job queued before that — or any caller
        // that still passes the blank Anonymous credentials — would only earn a 403, so say why instead.
        if (ctx.Credentials.IsAnonymous)
        {
            yield return new AttemptFailed(
                "DropMB no longer has anonymous upload — it answers 403 Forbidden to anyone who isn't signed in. "
                + "Add a DropMB account (username and password) in Account Manager.",
                null);
            yield break;
        }

        if (ctx.FileSize > MaxFileSizeBytes)
        {
            yield return new AttemptFailed(
                $"File exceeds DropMB's {ByteUnit.FromBytes(MaxFileSizeBytes, ByteBase.Decimal).ToFriendlyString()} per-share limit "
                + $"(this file is {ByteUnit.FromBytes(ctx.FileSize, ByteBase.Decimal).ToFriendlyString()}).",
                null);
            yield break;
        }

        // === The account ===
        // Every upload is signed in. A missing token has to stop the attempt here: sent anyway it earns a
        // 403, and were the host ever to re-open anonymous upload, the share would be filed under nobody
        // and still return a working link — which is not what an account upload promised.
        (string? signedIn, string? authError) = await ResolveTokenAsync(ctx);
        if (signedIn is null)
        {
            yield return new AttemptFailed(
                authError ?? "DropMB has no sign-in for this account, and DropMB refuses uploads that aren't "
                + "signed in. Re-check the account in Account Manager.",
                null);
            yield break;
        }

        AccessToken token = new(signedIn);

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

    private async Task<(bool Created, string? Error)> CreateShareAsync(AttemptContext ctx, string shareId, AccessToken token)
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
            response = await SendAuthenticatedAsync(ctx, token, tok => PostJsonAsync(ctx, SharesUrl, json, tok));
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
    private async Task<string?> SendChunksAsync(AttemptContext ctx, string shareId, AccessToken token)
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

            // The send can run twice (see SendAuthenticatedAsync), and the second run has to read the same
            // slice from its start — so it seeks to a position fixed here, not to one the loop moves on.
            long sliceStart = position;
            HttpResponseSnapshot response = await SendAuthenticatedAsync(ctx, token, async tok =>
            {
                if (_chunkOverride is not null)
                {
                    return await _chunkOverride(endpoint, Headers(tok), thisChunk);
                }

                file.Position = sliceStart;

                // PutChunkAsync already sends a raw octet-stream body with progress; the only
                // difference here is the verb, which this API wants as POST.
                return await ctx.Handler.PutChunkAsync(
                    endpoint,
                    new ChunkSliceStream(file, thisChunk),
                    thisChunk,
                    basePosition: sliceStart,
                    totalFileSize: fileSize,
                    dateTimeStarted: started,
                    ctx.SpeedBudget,
                    headers: Headers(tok),
                    cancellationToken: ctx.Cancellation,
                    method: HttpMethod.Post);
            });

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
    private async Task<string?> CompleteAsync(AttemptContext ctx, string shareId, AccessToken token)
    {
        try
        {
            HttpResponseSnapshot response = await SendAuthenticatedAsync(
                ctx, token, tok => PostJsonAsync(ctx, $"{SharesUrl}/{shareId}/complete", null, tok));
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

    /// <summary>The token to start an attempt with: one an earlier attempt signed in for, else the one
    /// stored on the account (only a first guess — usually long expired, see the class remarks), else a
    /// fresh sign-in with the saved username and password.</summary>
    private async Task<(string? Token, string? Error)> ResolveTokenAsync(AttemptContext ctx)
    {
        if (_freshTokens.TryGetValue(ctx.Credentials.Id, out string? known))
        {
            return (known, null);
        }

        if (NullIfWhiteSpace(ctx.Credentials.SessionCookie) is { } stored)
        {
            return (stored, null);
        }

        return await ReauthenticateAsync(ctx, rejected: null);
    }

    /// <summary>The token an attempt is using. A holder rather than a string because it can change
    /// part-way through: see <see cref="SendAuthenticatedAsync"/>.</summary>
    private sealed class AccessToken(string value)
    {
        public string Value { get; set; } = value;
    }

    /// <summary>
    /// Runs one request with the attempt's token. A 401 or 403 is how the host says "I don't know you" — and
    /// the 15-minute token it issued has usually just lapsed — so the first one signs in again and repeats
    /// the request, once. A second refusal is the host's real answer and comes back as it is, so a request it
    /// genuinely won't take is never retried forever. If the fresh sign-in itself fails, the original refusal
    /// is returned: it describes the request that was actually made.
    /// </summary>
    private async Task<HttpResponseSnapshot> SendAuthenticatedAsync(
        AttemptContext ctx,
        AccessToken token,
        Func<string, Task<HttpResponseSnapshot>> send)
    {
        HttpResponseSnapshot response = await send(token.Value);
        if (response.StatusCode is not (401 or 403))
        {
            return response;
        }

        (string? fresh, _) = await ReauthenticateAsync(ctx, rejected: token.Value);
        if (fresh is null)
        {
            return response;
        }

        token.Value = fresh;
        return await send(fresh);
    }

    /// <summary>
    /// Signs in with the account's saved username and password and remembers the token for the account.
    /// One sign-in at a time per account: an attempt that waited behind another takes the token that one
    /// minted instead of signing in again — unless that is the very token it was just refused.
    /// </summary>
    private async Task<(string? Token, string? Error)> ReauthenticateAsync(AttemptContext ctx, string? rejected)
    {
        int accountId = ctx.Credentials.Id;
        SemaphoreSlim gate = _signInGates.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ctx.Cancellation);
        try
        {
            if (_freshTokens.TryGetValue(accountId, out string? cached)
                && !string.Equals(cached, rejected, StringComparison.Ordinal))
            {
                return (cached, null);
            }

            (string? token, string? error) = await SignInAsync(
                ctx.Handler,
                ctx.Credentials.Username,
                ctx.Credentials.Password,
                ctx.Cancellation,
                (url, json, headers) => _postJsonOverride is not null
                    ? _postJsonOverride(url, json, headers)
                    : ctx.Handler.PostJsonAsync(url, json, headers, ctx.Cancellation));

            if (token is not null)
            {
                _freshTokens[accountId] = token;
            }

            return (token, error);
        }
        finally
        {
            gate.Release();
        }
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

        // The site's own sign-in form posts {email} when what was typed has an "@" and {username} otherwise,
        // and the host looks the account up by whichever it is given — so an email sent as a username is
        // never matched against the email column. People whose account came through Google/GitHub/Discord
        // tend to think of it by its email.
        string login = username.Trim();
        string json = login.Contains('@', StringComparison.Ordinal)
            ? JsonSerializer.Serialize(new { email = login, password })
            : JsonSerializer.Serialize(new { username = login, password });

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

        if (ReadAccessToken(response) is { } token)
        {
            return (token, null);
        }

        // An account with two-factor on answers 200 with a one-time {loginToken} to finish the sign-in with a
        // code, and no access token — which is not a fault in the password and not something to retry.
        return (null, response.Body.Contains("\"loginToken\"", StringComparison.Ordinal)
            ? "This DropMB account has two-factor sign-in switched on, which CSUploader can't complete. "
              + "Turn it off in DropMB's account settings to upload with this account."
            : "DropMB accepted the sign-in but issued no access token, so there is nothing to upload with.");
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

            // Deliberately NO expiry. The token's real life is 15 minutes (see the class remarks), and the
            // app reads any expiry on an account as "this sign-in is dead — switch the account off" at the
            // next start (FileHosterLoginDto.HasExpiredSession). That would disable a perfectly good account
            // over a token the upload doesn't depend on: a refused call signs in again with the saved
            // password. Null means "no stated expiry", which that check leaves alone.
            DerivedUsername: username);
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
