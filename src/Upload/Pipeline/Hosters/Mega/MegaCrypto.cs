// <copyright file="MegaCrypto.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CSUploader.Upload.Pipeline.Hosters.Mega;

/// <summary>
/// Low-level MEGA crypto primitives used by the transfer.it (MEGA) upload backend — AES-128 in
/// ECB (key wrap), CBC (attributes + the CBC-MAC fast path) and CTR (file encryption), plus the
/// a32 (big-endian uint32 array) and base64url helpers the protocol speaks in. A faithful port of
/// the Python reference's <c>crypto.py</c>; verified byte-for-byte against known-answer vectors in
/// <c>MegaCryptoTests</c>. Pure functions — no I/O.
/// </summary>
internal static class MegaCrypto
{
    public const int OneMiB = 1048576;

    // ---------- base64url ----------

    public static string B64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] B64UrlDecode(string s)
    {
        string t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t + new string('=', (4 - (t.Length % 4)) % 4));
    }

    // ---------- a32 (big-endian uint32 array) ----------

    public static byte[] A32ToBytes(uint[] a)
    {
        byte[] b = new byte[a.Length * 4];
        for (int i = 0; i < a.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i * 4), a[i]);
        }

        return b;
    }

    public static uint[] BytesToA32(byte[] b)
    {
        int padded = (b.Length + 3) & ~3;
        if (padded != b.Length)
        {
            byte[] tmp = new byte[padded];
            Array.Copy(b, tmp, b.Length);
            b = tmp;
        }

        uint[] a = new uint[padded / 4];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i * 4));
        }

        return a;
    }

    public static string A32ToB64(uint[] a) => B64UrlEncode(A32ToBytes(a));

    public static uint[] B64ToA32(string s) => BytesToA32(B64UrlDecode(s));

    public static uint[] RandA32(int n)
    {
        byte[] b = RandomNumberGenerator.GetBytes(n * 4);
        return BytesToA32(b);
    }

    // ---------- AES-ECB key wrap (MEGA encrypt_key / decrypt_key) ----------

    public static uint[] EncryptKeyEcb(byte[] keyBytes, uint[] dataA32) => EcbTransform(keyBytes, dataA32, encrypt: true);

    public static uint[] DecryptKeyEcb(byte[] keyBytes, uint[] dataA32) => EcbTransform(keyBytes, dataA32, encrypt: false);

    private static uint[] EcbTransform(byte[] keyBytes, uint[] dataA32, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        byte[] data = A32ToBytes(dataA32); // length is a multiple of 16 (4 uint32 per block)
        byte[] outBytes = encrypt ? aes.EncryptEcb(data, PaddingMode.None) : aes.DecryptEcb(data, PaddingMode.None);
        return BytesToA32(outBytes);
    }

    // ---------- node attribute encryption ----------

    /// <summary>MEGA attr key: <c>[k0^k4, k1^k5, k2^k6, k3^k7]</c>. For a 4-element key the tail is
    /// zero so it collapses to <c>k[:4]</c>.</summary>
    public static byte[] AttrKey(uint[] keyA32)
    {
        uint[] k = new uint[8];
        Array.Copy(keyA32, k, Math.Min(keyA32.Length, 8));
        return A32ToBytes([k[0] ^ k[4], k[1] ^ k[5], k[2] ^ k[6], k[3] ^ k[7]]);
    }

    /// <summary>Encrypt node attributes: <c>"MEGA" + compact-json</c>, zero-padded to 16, AES-CBC
    /// (IV = 0) with the attr key.</summary>
    public static byte[] EncryptAttr(object attrs, uint[] keyA32)
    {
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(attrs, MegaJson.CompactNoEscape));
        byte[] raw = new byte[4 + json.Length];
        "MEGA"u8.CopyTo(raw);
        json.CopyTo(raw, 4);
        int padded = (raw.Length + 15) & ~15;
        byte[] buf = new byte[padded];
        raw.CopyTo(buf, 0);

        using var aes = Aes.Create();
        aes.Key = AttrKey(keyA32);
        return aes.EncryptCbc(buf, new byte[16], PaddingMode.None);
    }

    // ---------- per-chunk AES-CTR encryption + CBC-MAC ----------

    /// <summary>
    /// Encrypt a chunk (≤ 1 MiB) with AES-CTR and compute its CBC-MAC. Matches MEGA's encrypter
    /// worker: key = ulKey[0..3], nonce = ulKey[4..5] (8 bytes); CTR IV = nonce||counter (counter in
    /// 16-byte blocks from byteOffset/16); the MAC is the last block of an AES-CBC of the zero-padded
    /// plaintext under IV = nonce||nonce (empty chunk → MAC = the IV).
    /// </summary>
    public static (byte[] Ciphertext, uint[] Mac) EncryptChunkAndMac(byte[] data, uint[] ulKey, long byteOffset)
    {
        byte[] keyBytes = A32ToBytes(ulKey[..4]);
        byte[] nonce = A32ToBytes(ulKey[4..6]); // 8 bytes

        byte[] ciphertext = AesCtr(keyBytes, nonce, byteOffset / 16, data);

        byte[] macBytes;
        if (data.Length > 0)
        {
            int padded = (data.Length + 15) & ~15;
            byte[] buf = new byte[padded];
            data.CopyTo(buf, 0);
            byte[] macIv = new byte[16];
            nonce.CopyTo(macIv, 0);
            nonce.CopyTo(macIv, 8);
            using var aes = Aes.Create();
            aes.Key = keyBytes;
            byte[] cbc = aes.EncryptCbc(buf, macIv, PaddingMode.None);
            macBytes = cbc[^16..];
        }
        else
        {
            macBytes = new byte[16];
            nonce.CopyTo(macBytes, 0);
            nonce.CopyTo(macBytes, 8);
        }

        return (ciphertext, BytesToA32(macBytes));
    }

    /// <summary>AES-CTR: 128-bit counter = <c>nonce(8) || counter64</c> (big-endian), starting at
    /// <paramref name="initialCounter"/> and incrementing per 16-byte block.</summary>
    private static byte[] AesCtr(byte[] keyBytes, byte[] nonce, long initialCounter, byte[] data)
    {
        byte[] output = new byte[data.Length];
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform enc = aes.CreateEncryptor();

        byte[] counterBlock = new byte[16];
        nonce.CopyTo(counterBlock, 0);
        byte[] keystream = new byte[16];
        ulong counter = (ulong)initialCounter;

        for (int offset = 0; offset < data.Length; offset += 16)
        {
            BinaryPrimitives.WriteUInt64BigEndian(counterBlock.AsSpan(8), counter);
            enc.TransformBlock(counterBlock, 0, 16, keystream, 0);
            int n = Math.Min(16, data.Length - offset);
            for (int i = 0; i < n; i++)
            {
                output[offset + i] = (byte)(data[offset + i] ^ keystream[i]);
            }

            counter++;
        }

        return output;
    }

    /// <summary>XOR each per-chunk MAC into the accumulator, AES-ECB-encrypting between.</summary>
    public static uint[] CondenseMacs(IReadOnlyList<uint[]> macs, uint[] ulKey)
    {
        uint[] acc = [0, 0, 0, 0];
        byte[] keyBytes = A32ToBytes(ulKey[..4]);
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;

        foreach (uint[] m in macs)
        {
            for (int j = 0; j + 3 < m.Length; j += 4)
            {
                acc = [acc[0] ^ m[j], acc[1] ^ m[j + 1], acc[2] ^ m[j + 2], acc[3] ^ m[j + 3]];
                acc = BytesToA32(aes.EncryptEcb(A32ToBytes(acc), PaddingMode.None));
            }
        }

        return acc;
    }

    /// <summary>Build the 8-element MEGA file key from the upload key and condensed MAC (the layout
    /// MEGA stores and the web client expects).</summary>
    public static uint[] BuildFileKey(uint[] ulKey, uint[] mac) =>
    [
        ulKey[0] ^ ulKey[4],
        ulKey[1] ^ ulKey[5],
        ulKey[2] ^ mac[0] ^ mac[1],
        ulKey[3] ^ mac[2] ^ mac[3],
        ulKey[4],
        ulKey[5],
        mac[0] ^ mac[1],
        mac[2] ^ mac[3],
    ];

    // ---------- CRC-32 (zlib / ISO-HDLC; MEGA's crc32b) ----------

    private static readonly uint[] _crcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    /// <summary>CRC-32 matching Python's <c>zlib.crc32(data, init)</c> — chainable so
    /// <c>Crc32b(b, Crc32b(a)) == Crc32b(a+b)</c>.</summary>
    public static uint Crc32b(ReadOnlySpan<byte> data, uint init = 0)
    {
        uint crc = init ^ 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    // ---------- chunk sizing (MEGA's chunkmap) ----------

    /// <summary>The chunk (offset, length) list for a file of <paramref name="size"/> bytes. First 8
    /// chunks grow 128 KiB → 1 MiB; everything after is 1 MiB. Also reports whether an empty tail
    /// frame is needed (size 0, or the last chunk was a full nominal size).</summary>
    public static (List<(long Offset, int Length)> Chunks, bool NeedEmptyTail) IterChunks(long size)
    {
        List<(long, int)> chunks = [];
        long pos = 0;
        bool truncatedLast = false;
        while (pos < size)
        {
            int nominal = ChunkmapNominal(pos);
            long remaining = size - pos;
            if (remaining < nominal)
            {
                chunks.Add((pos, (int)remaining));
                pos += remaining;
                truncatedLast = true;
            }
            else
            {
                chunks.Add((pos, nominal));
                pos += nominal;
                truncatedLast = false;
            }
        }

        bool needEmptyTail = size == 0 || !truncatedLast;
        return (chunks, needEmptyTail);
    }

    /// <summary>Nominal chunk length at <paramref name="pos"/>: the 128 KiB-stepped sizes for the
    /// first 8 boundaries, then 1 MiB. Mirrors the reference's CHUNKMAP dict lookup with a 1 MiB
    /// default.</summary>
    private static int ChunkmapNominal(long pos)
    {
        long p = 0;
        int dp = 0;
        while (dp < OneMiB)
        {
            dp += 131072;
            if (p == pos)
            {
                return dp;
            }

            p += dp;
        }

        return OneMiB;
    }
}

/// <summary>Shared JSON options for MEGA attribute encoding — compact (no spaces) and no extra
/// escaping, matching Python's <c>json.dumps(separators=(",",":"))</c>.</summary>
internal static class MegaJson
{
    public static readonly JsonSerializerOptions CompactNoEscape = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
