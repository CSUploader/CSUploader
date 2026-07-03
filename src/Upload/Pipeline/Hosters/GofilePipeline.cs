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
///   <item><c>POST https://api.gofile.io/accounts</c> (no body) → a guest account with a
///   <c>token</c>.</item>
///   <item><c>GET https://api.gofile.io/accounts/website</c> (<c>Bearer token</c>) → the account
///   info, whose <c>rootFolder</c> is the parent for the upload folder.</item>
///   <item><c>POST https://api.gofile.io/contents/createfolder</c> (<c>Bearer token</c>,
///   <c>{parentFolderId: rootFolder, public: true}</c>) → a fresh public folder (its <c>id</c> is the
///   upload target, its <c>code</c> is the share slug).</item>
///   <item><c>POST https://upload.gofile.io/uploadfile</c> (multipart <c>token</c> + <c>folderId</c> +
///   <c>file</c>) → the file; the share link is the response's <c>downloadPage</c>
///   (<c>https://gofile.io/d/&lt;code&gt;</c>).</item>
/// </list>
/// The first three steps create no file, so a mid-send upload fault is safe to retry (a fresh guest
/// account + folder). No hashing, no account, no size cap (gofile enforces its own).
/// </summary>
public sealed class GofilePipeline : IFileHosterPipeline
{
    private const string ApiBase = "https://api.gofile.io";
    private const string UploadUrl = "https://upload.gofile.io/uploadfile";
    private const string Origin = "https://gofile.io";

    private readonly Func<HttpMethod, string, string?, string?, Task<HttpResponseSnapshot>>? _apiOverride;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, Task<HttpResponseSnapshot>>? _uploadOverride;

    public GofilePipeline()
    {
    }

    /// <summary>Test ctor — stubs the JSON API calls (accounts / accounts-website / createfolder) and
    /// the multipart upload so the orchestration runs without the network. The <c>api</c> stub receives
    /// (method, url, jsonBody, bearerToken).</summary>
    internal GofilePipeline(
        Func<HttpMethod, string, string?, string?, HttpResponseSnapshot> api,
        Func<string, string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>?, Func<long?>?, HttpResponseSnapshot> upload)
    {
        _apiOverride = (m, u, j, t) => Task.FromResult(api(m, u, j, t));
        _uploadOverride = (fp, u, f, h, s) => Task.FromResult(upload(fp, u, f, h, s));
    }

    public string Name => "Gofile";

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

        // === Phase 1: guest account → rootFolder → a fresh public folder to upload into ===
        string? token = null;
        string? folderId = null;
        string? setupError = null;
        try
        {
            token = await CreateGuestAccountAsync(ctx);
            string rootFolder = await FetchRootFolderAsync(ctx, token);
            folderId = await CreateFolderAsync(ctx, token, rootFolder);
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

        Task<HttpResponseSnapshot> uploadTask = UploadAsync(ctx, token!, folderId!);
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

    /// <summary>POST /accounts (no body) → a guest account; returns its token.</summary>
    private async Task<string> CreateGuestAccountAsync(AttemptContext ctx)
    {
        HttpResponseSnapshot snap = await ApiAsync(ctx, HttpMethod.Post, ApiBase + "/accounts", json: null, bearer: null);
        return RequireDataString(snap, "token", "accounts");
    }

    /// <summary>GET /accounts/website (Bearer) → account info; returns its rootFolder id.</summary>
    private async Task<string> FetchRootFolderAsync(AttemptContext ctx, string token)
    {
        HttpResponseSnapshot snap = await ApiAsync(ctx, HttpMethod.Get, ApiBase + "/accounts/website", json: null, bearer: token);
        return RequireDataString(snap, "rootFolder", "accounts/website");
    }

    /// <summary>POST /contents/createfolder (Bearer) → a fresh public folder; returns its id.</summary>
    private async Task<string> CreateFolderAsync(AttemptContext ctx, string token, string rootFolder)
    {
        string body = JsonSerializer.Serialize(new { parentFolderId = rootFolder, @public = true });
        HttpResponseSnapshot snap = await ApiAsync(ctx, HttpMethod.Post, ApiBase + "/contents/createfolder", body, bearer: token);
        return RequireDataString(snap, "id", "createfolder");
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
