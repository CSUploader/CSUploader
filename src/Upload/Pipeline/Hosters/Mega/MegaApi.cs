// <copyright file="MegaApi.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline.Hosters.Mega;

/// <summary>A MEGA bt7 API numeric error (e.g. -9 ENOENT, -15 ESID). <see cref="Code"/> is the raw
/// negative code; the message is a friendly default.</summary>
internal sealed class MegaApiException(int code, string? message = null)
    : Exception(message ?? CodeName(code))
{
    public int Code { get; } = code;

    private static string CodeName(int code) => code switch
    {
        -3 => "MEGA server busy (EAGAIN)",
        -8 => "transfer expired",
        -9 => "transfer not found (ENOENT)",
        -11 => "access denied (EACCESS)",
        -15 => "invalid session (ESID)",
        -17 => "quota exceeded (EOVERQUOTA)",
        _ => $"MEGA API error {code}",
    };
}

/// <summary>A WebSocket upload pool from <c>usc</c>: a storage host, the upload URI path, and the
/// max file size it accepts (0 = no limit).</summary>
internal readonly record struct MegaUploadPool(string Host, string Uri, long Limit);

/// <summary>
/// Stateful client for MEGA's bt7 API used by transfer.it (<c>bt7.api.mega.co.nz/cs</c>). Ported
/// from the transfer-it-cli reference (<c>api.py</c>), with the wire shapes reconciled against a live
/// transfer.it capture: anonymous ephemeral-account handshake (<c>up</c>/<c>us</c>), transfer verbs
/// (<c>xn</c>/<c>xp</c>/<c>xc</c>), and the upload-pool list (<c>usc</c>). All crypto lives in
/// <see cref="MegaCrypto"/>. Requests are POSTed via an injected delegate so the app's HttpHandler
/// (proxy + UA) carries them and so the parsing is unit-testable.
/// </summary>
internal sealed class MegaApi
{
    public const string ApiBase = "https://bt7.api.mega.co.nz/";
    public const string ShareBase = "https://transfer.it";

    private readonly Func<string, string, CancellationToken, Task<HttpResponseSnapshot>> _postJson;
    private readonly Func<uint[]> _randKey;
    private readonly string _base;
    private long _seqno = RandomNumberGenerator.GetInt32(1_000_000_000);

    public string? Sid { get; private set; }

    public MegaApi(
        Func<string, string, CancellationToken, Task<HttpResponseSnapshot>> postJson,
        string? apiBase = null,
        Func<uint[]>? randKey = null)
    {
        _postJson = postJson;
        _base = apiBase ?? ApiBase;
        _randKey = randKey ?? (() => MegaCrypto.RandA32(4));
    }

    // ------------------------------------------------------------------ request plumbing

    /// <summary>Issue a single bt7 command. Returns the first element of the response array (the
    /// result for this command). Throws <see cref="MegaApiException"/> on a negative code, with a
    /// short retry on -3 (EAGAIN).</summary>
    public async Task<JsonElement> ReqAsync(object payload, CancellationToken ct)
    {
        long id = NextSeqno();
        string url = _base + "cs?id=" + id.ToString(CultureInfo.InvariantCulture);
        if (Sid is not null)
        {
            url += "&sid=" + Uri.EscapeDataString(Sid);
        }

        string body = JsonSerializer.Serialize(new[] { payload }, MegaJson.CompactNoEscape);

        for (int attempt = 0; ; attempt++)
        {
            HttpResponseSnapshot snap = await _postJson(url, body, ct).ConfigureAwait(false);
            if (snap.StatusCode is < 200 or >= 300)
            {
                throw new MegaApiException(0, $"MEGA API HTTP {snap.StatusCode}: {Snippet(snap.Body)}");
            }

            using JsonDocument doc = JsonDocument.Parse(snap.Body);
            JsonElement root = doc.RootElement;

            int? code = ErrorCode(root);
            if (code is int c && c < 0)
            {
                if (c == -3 && attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 + attempt), ct).ConfigureAwait(false);
                    continue;
                }

                throw new MegaApiException(c);
            }

            JsonElement result = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            return result.Clone();
        }
    }

    private static int? ErrorCode(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Number)
        {
            return root.GetInt32();
        }

        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1 && root[0].ValueKind == JsonValueKind.Number)
        {
            return root[0].GetInt32();
        }

        return null;
    }

    // ------------------------------------------------------------------ anonymous ephemeral session

    /// <summary>Create an anonymous ephemeral MEGA account (<c>up</c>/<c>us</c> key ceremony) and
    /// attach the session id. Returns the 128-bit master key (a32).</summary>
    public async Task<uint[]> CreateEphemeralSessionAsync(CancellationToken ct)
    {
        uint[] masterKey = _randKey();
        uint[] pwKey = _randKey();
        uint[] ssc = _randKey();

        uint[] kEnc = MegaCrypto.EncryptKeyEcb(MegaCrypto.A32ToBytes(pwKey), masterKey);
        uint[] sscEnc = MegaCrypto.EncryptKeyEcb(MegaCrypto.A32ToBytes(masterKey), ssc);
        byte[] ts = [.. MegaCrypto.A32ToBytes(ssc), .. MegaCrypto.A32ToBytes(sscEnc)];

        JsonElement upRes = await ReqAsync(
            new { a = "up", k = MegaCrypto.A32ToB64(kEnc), ts = MegaCrypto.B64UrlEncode(ts) },
            ct).ConfigureAwait(false);
        if (upRes.ValueKind != JsonValueKind.String)
        {
            throw new MegaApiException(0, $"up returned unexpected: {upRes}");
        }

        string userHandle = upRes.GetString()!;
        JsonElement usRes = await ReqAsync(new { a = "us", user = userHandle }, ct).ConfigureAwait(false);
        if (usRes.ValueKind != JsonValueKind.Object || !usRes.TryGetProperty("tsid", out JsonElement tsidEl))
        {
            throw new MegaApiException(0, $"us returned unexpected: {usRes}");
        }

        string tsidB64 = tsidEl.GetString()!;
        byte[] tsid = MegaCrypto.B64UrlDecode(tsidB64);
        uint[] checkEnc = MegaCrypto.EncryptKeyEcb(MegaCrypto.A32ToBytes(masterKey), MegaCrypto.BytesToA32(tsid[..16]));
        if (!MegaCrypto.A32ToBytes(checkEnc).AsSpan().SequenceEqual(tsid.AsSpan(tsid.Length - 16)))
        {
            throw new MegaApiException(0, "tsid verification failed");
        }

        Sid = tsidB64;
        return masterKey;
    }

    // ------------------------------------------------------------------ transfer verbs

    /// <summary>Create a transfer container (<c>xn</c>). Returns (transfer handle, root node handle,
    /// folder key). Tolerates both the bare <c>[xh, h]</c> and the status-wrapped <c>[0, [xh, h]]</c>
    /// response shapes.</summary>
    public async Task<(string Xh, string RootHandle, uint[] FolderKey)> CreateTransferAsync(string name, CancellationToken ct)
    {
        uint[] folderKey = _randKey();
        long mtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string at = MegaCrypto.B64UrlEncode(MegaCrypto.EncryptAttr(new { name, mtime }, folderKey));
        string k = MegaCrypto.A32ToB64(folderKey);

        JsonElement res = await ReqAsync(new { a = "xn", at, k }, ct).ConfigureAwait(false);
        (string xh, string h) = ParseXnResult(res);
        return (xh, h, folderKey);
    }

    private static (string Xh, string RootHandle) ParseXnResult(JsonElement res)
    {
        if (res.ValueKind != JsonValueKind.Array)
        {
            throw new MegaApiException(0, $"xn returned unexpected: {res}");
        }

        // Bare [xh, h].
        if (res.GetArrayLength() == 2 && res[0].ValueKind == JsonValueKind.String && res[1].ValueKind == JsonValueKind.String)
        {
            return (res[0].GetString()!, res[1].GetString()!);
        }

        // Status-wrapped [status, [xh, h]] (transfer.it's current shape).
        if (res.GetArrayLength() == 2 && res[1].ValueKind == JsonValueKind.Array && res[1].GetArrayLength() == 2)
        {
            return (res[1][0].GetString()!, res[1][1].GetString()!);
        }

        throw new MegaApiException(0, $"xn returned unexpected: {res}");
    }

    /// <summary>The WebSocket upload pool list (<c>usc</c>).</summary>
    public async Task<List<MegaUploadPool>> UploadPoolsAsync(CancellationToken ct)
    {
        JsonElement res = await ReqAsync(new { a = "usc" }, ct).ConfigureAwait(false);
        if (res.ValueKind != JsonValueKind.Array)
        {
            throw new MegaApiException(0, $"usc returned unexpected: {res}");
        }

        List<MegaUploadPool> pools = [];
        foreach (JsonElement entry in res.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2)
            {
                continue;
            }

            long limit = entry.GetArrayLength() > 2 && entry[2].ValueKind == JsonValueKind.Number ? entry[2].GetInt64() : 0;
            pools.Add(new MegaUploadPool(entry[0].GetString()!, entry[1].GetString()!, limit));
        }

        if (pools.Count == 0)
        {
            throw new MegaApiException(0, "usc returned no upload pools");
        }

        return pools;
    }

    /// <summary>Picks the first pool whose limit fits the file (or has no limit).</summary>
    public static MegaUploadPool PickPool(IReadOnlyList<MegaUploadPool> pools, long size)
    {
        foreach (MegaUploadPool p in pools)
        {
            if (p.Limit == 0 || size <= p.Limit)
            {
                return p;
            }
        }

        throw new MegaApiException(0, "no upload pool accepts a file this size");
    }

    /// <summary>Attach a freshly-uploaded file to a transfer (<c>xp</c>, v3). Returns the node handle
    /// the server assigned.</summary>
    public async Task<string> FinaliseFileAsync(
        string transferRoot, byte[] completionToken, uint[] ulKey, IReadOnlyList<uint[]> macsOrdered, string filename, CancellationToken ct)
    {
        uint[] mac = MegaCrypto.CondenseMacs(macsOrdered, ulKey);
        uint[] fileKey = MegaCrypto.BuildFileKey(ulKey, mac);

        string at = MegaCrypto.B64UrlEncode(MegaCrypto.EncryptAttr(new { n = filename }, fileKey));
        string k = MegaCrypto.A32ToB64(fileKey);
        string h = MegaCrypto.B64UrlEncode(completionToken);

        JsonElement res = await ReqAsync(
            new { a = "xp", v = 3, t = transferRoot, n = new[] { new { t = 0, h, a = at, k } } },
            ct).ConfigureAwait(false);

        if (res.ValueKind != JsonValueKind.Object
            || !res.TryGetProperty("f", out JsonElement f)
            || f.ValueKind != JsonValueKind.Array
            || f.GetArrayLength() == 0
            || !f[0].TryGetProperty("h", out JsonElement nodeH))
        {
            throw new MegaApiException(0, $"xp returned unexpected: {res}");
        }

        return nodeH.GetString()!;
    }

    /// <summary>Close a transfer (<c>xc</c>) — makes it read-only / shareable.</summary>
    public Task CloseTransferAsync(string xh, CancellationToken ct) => ReqAsync(new { a = "xc", xh }, ct);

    /// <summary>Set the transfer's display title (<c>xm</c>, base64url UTF-8).</summary>
    public Task SetTransferTitleAsync(string xh, string title, CancellationToken ct)
        => ReqAsync(new { a = "xm", xh, t = MegaCrypto.B64UrlEncode(System.Text.Encoding.UTF8.GetBytes(title.Trim())) }, ct);

    // ------------------------------------------------------------------ helpers

    private long NextSeqno() => Interlocked.Increment(ref _seqno);

    private static string Snippet(string body)
        => body.Length > 200 ? body[..200] + "…" : body;
}
