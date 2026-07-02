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
        -9 => "not found (ENOENT)",
        -11 => "access denied (EACCESS)",
        -15 => "invalid session (ESID)",
        -16 => "account blocked (EBLOCKED)",
        -17 => "quota exceeded (EOVERQUOTA)",
        -26 => "two-factor authentication required (EMFAREQUIRED)",
        _ => $"MEGA API error {code}",
    };
}

/// <summary>A WebSocket upload pool from <c>usc</c>: a storage host, the upload URI path, and the
/// max file size it accepts (0 = no limit).</summary>
internal readonly record struct MegaUploadPool(string Host, string Uri, long Limit);

/// <summary>
/// Stateful client for MEGA's command API, serving two frontends: transfer.it
/// (<c>bt7.api.mega.co.nz</c> — anonymous ephemeral handshake <c>up</c>/<c>us</c> + transfer verbs
/// <c>xn</c>/<c>xp</c>/<c>xc</c>, ported from the transfer-it-cli reference) and mega.nz proper
/// (<c>g.api.mega.co.nz</c> — password login <c>us0</c>/<c>us</c>, node verbs <c>f</c>/<c>p</c>/<c>l</c>,
/// quota <c>uq</c>, wire shapes reconciled against a live mega.nz web capture). The upload-pool
/// list (<c>usc</c>) and the WebSocket chunk upload are shared by both. All crypto lives in
/// <see cref="MegaCrypto"/>/<see cref="MegaLoginCrypto"/>. Requests are POSTed via an injected
/// delegate so the app's HttpHandler (proxy + UA) carries them and so the parsing is unit-testable.
/// </summary>
internal sealed class MegaApi
{
    public const string ApiBase = "https://bt7.api.mega.co.nz/";
    public const string ShareBase = "https://transfer.it";

    /// <summary>mega.nz proper (account uploads) speaks to <c>g.api</c>, not transfer.it's
    /// <c>bt7.api</c>. Same command plumbing, different host.</summary>
    public const string MegaNzApiBase = "https://g.api.mega.co.nz/";
    public const string MegaNzShareBase = "https://mega.nz";

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

            using var doc = JsonDocument.Parse(snap.Body);
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

    // ------------------------------------------------------------------ mega.nz account session

    /// <summary>
    /// Log in to a real mega.nz account (<c>us0</c> → derive → <c>us</c>) and attach the session
    /// id. Returns the account master key (bytes). v2 accounts use the PBKDF2 derivation with the
    /// server salt; anything else falls back to the v1 legacy derivation. 2FA-protected accounts
    /// fail with <see cref="MegaApiException"/> −26.
    /// </summary>
    public async Task<byte[]> LoginAsync(string email, string password, CancellationToken ct)
    {
        email = email.Trim().ToLowerInvariant();

        int version = 1;
        string? saltB64 = null;
        try
        {
            JsonElement pre = await ReqAsync(new { a = "us0", user = email }, ct).ConfigureAwait(false);
            if (pre.ValueKind == JsonValueKind.Object)
            {
                version = pre.TryGetProperty("v", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 1;
                saltB64 = pre.TryGetProperty("s", out JsonElement s) ? s.GetString() : null;
            }
        }
        catch (MegaApiException)
        {
            // us0 unsupported / unknown for this account shape — treat as a v1 legacy login.
        }

        byte[] pwKey;
        string uh;
        if (version == 2 && saltB64 is not null)
        {
            (pwKey, uh) = MegaLoginCrypto.DeriveV2(password, MegaCrypto.B64UrlDecode(saltB64));
        }
        else
        {
            pwKey = MegaLoginCrypto.PrepareKeyV1(password);
            uh = MegaLoginCrypto.StringHashV1(email, pwKey);
        }

        JsonElement us = await ReqAsync(new { a = "us", user = email, uh }, ct).ConfigureAwait(false);
        if (us.ValueKind != JsonValueKind.Object
            || !us.TryGetProperty("k", out JsonElement kEl)
            || !us.TryGetProperty("privk", out JsonElement privkEl)
            || !us.TryGetProperty("csid", out JsonElement csidEl))
        {
            throw new MegaApiException(0, $"us returned unexpected: {us}");
        }

        byte[] masterKey = MegaLoginCrypto.DecryptMasterKey(kEl.GetString()!, pwKey);
        Sid = MegaLoginCrypto.DecryptSessionId(privkEl.GetString()!, csidEl.GetString()!, masterKey);
        return masterKey;
    }

    /// <summary>Fetch the account's node tree (<c>f</c>) and return the Cloud Drive root handle
    /// (the node with type 2) — the upload target for new files.</summary>
    public async Task<string> FetchCloudRootAsync(CancellationToken ct)
    {
        JsonElement res = await ReqAsync(new { a = "f", c = 1 }, ct).ConfigureAwait(false);
        if (res.ValueKind == JsonValueKind.Object && res.TryGetProperty("f", out JsonElement nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement node in nodes.EnumerateArray())
            {
                if (node.TryGetProperty("t", out JsonElement t) && t.ValueKind == JsonValueKind.Number && t.GetInt32() == 2
                    && node.TryGetProperty("h", out JsonElement h))
                {
                    return h.GetString()!;
                }
            }
        }

        throw new MegaApiException(0, "f returned no Cloud Drive root node");
    }

    /// <summary>
    /// Attach a freshly-uploaded file to the account's Cloud Drive (<c>p</c>, classic synchronous
    /// shape — no <c>i</c>/<c>v</c> so the new node comes back in <c>f</c> instead of deferring to
    /// the action-packet channel). The node key is the condensed-MAC file key wrapped with the
    /// account master key. Returns the new node's handle and the (plain) file key for the share
    /// link fragment.
    /// </summary>
    public async Task<(string NodeHandle, uint[] FileKey)> PutFileNodeAsync(
        string parentHandle, byte[] completionToken, uint[] ulKey, IReadOnlyList<uint[]> macsOrdered, string filename, byte[] masterKey, CancellationToken ct)
    {
        uint[] mac = MegaCrypto.CondenseMacs(macsOrdered, ulKey);
        uint[] fileKey = MegaCrypto.BuildFileKey(ulKey, mac);

        string at = MegaCrypto.B64UrlEncode(MegaCrypto.EncryptAttr(new { n = filename }, fileKey));
        string k = MegaCrypto.A32ToB64(MegaCrypto.EncryptKeyEcb(masterKey, fileKey));
        string h = MegaCrypto.B64UrlEncode(completionToken);

        JsonElement res = await ReqAsync(
            new { a = "p", t = parentHandle, n = new[] { new { t = 0, h, a = at, k } } },
            ct).ConfigureAwait(false);

        if (res.ValueKind != JsonValueKind.Object
            || !res.TryGetProperty("f", out JsonElement f)
            || f.ValueKind != JsonValueKind.Array
            || f.GetArrayLength() == 0
            || !f[0].TryGetProperty("h", out JsonElement nodeH))
        {
            throw new MegaApiException(0, $"p returned unexpected: {res}");
        }

        return (nodeH.GetString()!, fileKey);
    }

    /// <summary>Create (or fetch) the node's public link handle (<c>l</c>). The share link is
    /// <c>https://mega.nz/file/&lt;ph&gt;#&lt;fileKeyB64&gt;</c> — the key never reaches the server.</summary>
    public async Task<string> ExportNodeAsync(string nodeHandle, CancellationToken ct)
    {
        JsonElement res = await ReqAsync(new { a = "l", n = nodeHandle }, ct).ConfigureAwait(false);
        if (res.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(res.GetString()))
        {
            return res.GetString()!;
        }

        throw new MegaApiException(0, $"l returned unexpected: {res}");
    }

    /// <summary>Storage numbers (<c>uq</c>): used bytes, total bytes, and whether the account is a
    /// paid tier (<c>utype</c> &gt; 0).</summary>
    public async Task<(long UsedBytes, long TotalBytes, bool IsPaid)> QuotaAsync(CancellationToken ct)
    {
        // pro:1 + v:2 mirror the web client's uq — they surface utype for paid tiers (a free account's
        // response simply omits it, and IsPaid stays false).
        JsonElement res = await ReqAsync(new { a = "uq", strg = 1, pro = 1, v = 2 }, ct).ConfigureAwait(false);
        if (res.ValueKind != JsonValueKind.Object
            || !res.TryGetProperty("cstrg", out JsonElement used)
            || !res.TryGetProperty("mstrg", out JsonElement total))
        {
            throw new MegaApiException(0, $"uq returned unexpected: {res}");
        }

        bool paid = res.TryGetProperty("utype", out JsonElement utype) && utype.ValueKind == JsonValueKind.Number && utype.GetInt32() > 0;
        return (used.GetInt64(), total.GetInt64(), paid);
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
