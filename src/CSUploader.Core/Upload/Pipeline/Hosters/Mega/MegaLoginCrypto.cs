// <copyright file="MegaLoginCrypto.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace CSUploader.Upload.Pipeline.Hosters.Mega;

/// <summary>
/// MEGA password-login crypto (the <c>us0</c>/<c>us</c> ceremony for a real mega.nz account, as
/// opposed to the anonymous ephemeral session transfer.it uses). Faithful port of the algorithms
/// in the MEGA web SDK / mega.py, verified against Node-generated known-answer vectors in
/// <c>MegaLoginCryptoTests</c>:
/// <list type="bullet">
///   <item>v2 accounts (2018+): PBKDF2-HMAC-SHA512(password, salt, 100000) → 32 bytes; the first
///   16 are the password AES key, the last 16 the <c>uh</c> login proof.</item>
///   <item>v1 (legacy) accounts: <see cref="PrepareKeyV1"/> (65536 AES-ECB rounds keyed by the
///   password words) + <see cref="StringHashV1"/> (email hash under that key).</item>
///   <item>The session id arrives RSA-encrypted (<c>csid</c>); the RSA private key arrives
///   AES-wrapped under the master key (<c>privk</c>, four MPIs: p, q, d, u).</item>
/// </list>
/// Pure functions — no I/O.
/// </summary>
internal static class MegaLoginCrypto
{
    /// <summary>v2 key derivation. Returns the 16-byte password AES key (decrypts <c>k</c>) and
    /// the base64url <c>uh</c> proof sent with the <c>us</c> command.</summary>
    public static (byte[] PasswordKey, string UserHashB64) DeriveV2(string password, byte[] salt)
    {
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, 100000, HashAlgorithmName.SHA512, 32);
        return (derived[..16], MegaCrypto.B64UrlEncode(derived[16..]));
    }

    /// <summary>v1 (legacy) password key: 65536 rounds of single-block AES-ECB over a fixed IV,
    /// keyed by each 4-word slice of the password (zero-padded). The per-slice keys never change
    /// across rounds, so each slice's AES instance is built once.</summary>
    public static byte[] PrepareKeyV1(string password)
    {
        uint[] arr = StrToA32(password);
        byte[] pkey = MegaCrypto.A32ToBytes([0x93C467E3, 0x7DB0C7A4, 0xD1BE3F81, 0x0152CB56]);

        List<Aes> slices = [];
        try
        {
            for (int j = 0; j < arr.Length; j += 4)
            {
                uint[] key = new uint[4];
                for (int i = 0; i < 4 && j + i < arr.Length; i++)
                {
                    key[i] = arr[j + i];
                }

                Aes aes = Aes.Create();
                aes.Key = MegaCrypto.A32ToBytes(key);
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                slices.Add(aes);
            }

            for (int r = 0; r < 0x10000; r++)
            {
                foreach (Aes aes in slices)
                {
                    pkey = aes.EncryptEcb(pkey, PaddingMode.None);
                }
            }

            return pkey;
        }
        finally
        {
            foreach (Aes aes in slices)
            {
                aes.Dispose();
            }
        }
    }

    /// <summary>v1 login proof: XOR-fold the (lowercased) email's words into a 4-word state, run
    /// 16384 AES-ECB rounds under the password key, emit words 0 and 2 as base64url.</summary>
    public static string StringHashV1(string email, byte[] passwordKey)
    {
        uint[] s32 = StrToA32(email);
        uint[] h32 = new uint[4];
        for (int i = 0; i < s32.Length; i++)
        {
            h32[i % 4] ^= s32[i];
        }

        byte[] h = MegaCrypto.A32ToBytes(h32);
        using var aes = Aes.Create();
        aes.Key = passwordKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        for (int r = 0; r < 0x4000; r++)
        {
            h = aes.EncryptEcb(h, PaddingMode.None);
        }

        uint[] hw = MegaCrypto.BytesToA32(h);
        return MegaCrypto.B64UrlEncode(MegaCrypto.A32ToBytes([hw[0], hw[2]]));
    }

    /// <summary>Unwrap the account master key from the <c>us</c> response's <c>k</c> (AES-ECB under
    /// the password key).</summary>
    public static byte[] DecryptMasterKey(string kB64, byte[] passwordKey)
        => MegaCrypto.A32ToBytes(MegaCrypto.DecryptKeyEcb(passwordKey, MegaCrypto.B64ToA32(kB64)));

    /// <summary>
    /// Recover the session id: AES-unwrap <paramref name="privkB64"/> with the master key, parse
    /// the four RSA MPIs (p, q, d, u — u unused), and decrypt <paramref name="csidB64"/> as
    /// m = c^d mod pq. The sid is the first 43 bytes of m's minimal big-endian form (the mega.py
    /// hex round-trip, which strips leading zero bytes), base64url-encoded.
    /// </summary>
    public static string DecryptSessionId(string privkB64, string csidB64, byte[] masterKey)
    {
        byte[] privk = MegaCrypto.A32ToBytes(MegaCrypto.DecryptKeyEcb(masterKey, MegaCrypto.B64ToA32(privkB64)));

        int offset = 0;
        BigInteger p = ReadMpi(privk, ref offset);
        BigInteger q = ReadMpi(privk, ref offset);
        BigInteger d = ReadMpi(privk, ref offset);
        _ = ReadMpi(privk, ref offset); // u = p^-1 mod q — CRT-only, not needed for plain ModPow

        int csidOffset = 0;
        BigInteger c = ReadMpi(MegaCrypto.B64UrlDecode(csidB64), ref csidOffset);

        BigInteger m = BigInteger.ModPow(c, d, p * q);
        byte[] mBytes = m.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (mBytes.Length < 43)
        {
            throw new CryptographicException("MEGA csid decrypt produced a block too short for a session id");
        }

        return MegaCrypto.B64UrlEncode(mBytes[..43]);
    }

    /// <summary>Read one MEGA MPI (2-byte big-endian bit count, then ceil(bits/8) magnitude bytes)
    /// as an unsigned big-endian integer, advancing <paramref name="offset"/>.</summary>
    private static BigInteger ReadMpi(byte[] data, ref int offset)
    {
        if (offset + 2 > data.Length)
        {
            throw new CryptographicException("MEGA MPI truncated (missing bit-count prefix)");
        }

        int bits = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
        int len = (bits + 7) / 8;
        offset += 2;
        if (offset + len > data.Length)
        {
            throw new CryptographicException("MEGA MPI truncated (magnitude runs past the buffer)");
        }

        var value = new BigInteger(data.AsSpan(offset, len), isUnsigned: true, isBigEndian: true);
        offset += len;
        return value;
    }

    /// <summary>UTF-8 bytes packed into big-endian 32-bit words, zero-padded (MEGA's
    /// <c>str_to_a32</c>).</summary>
    private static uint[] StrToA32(string s) => MegaCrypto.BytesToA32(Encoding.UTF8.GetBytes(s));
}
