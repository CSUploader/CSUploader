// <copyright file="MegaCryptoTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload.Pipeline.Hosters.Mega;

namespace CSUploader.Tests.Upload.Pipeline.Hosters.Mega;

/// <summary>
/// Known-answer tests for <see cref="MegaCrypto"/>. Every expected value here was produced by the
/// Python reference's <c>crypto.py</c> (transfer-it-cli) with the same fixed inputs, so a passing
/// suite means the C# port matches MEGA's exact byte layout — the prerequisite for an upload that
/// actually decrypts on the other side.
/// </summary>
public class MegaCryptoTests
{
    [Fact]
    public void A32ToB64_MatchesReference()
        => Assert.Equal("AAAAAQAAAAIAAAADAAAABA", MegaCrypto.A32ToB64([1, 2, 3, 4]));

    [Fact]
    public void A32_Bytes_RoundTrip()
    {
        uint[] a = [0xDEADBEEF, 0x00112233, 1, 0xFFFFFFFF];
        Assert.Equal(a, MegaCrypto.BytesToA32(MegaCrypto.A32ToBytes(a)));
    }

    [Fact]
    public void B64Url_RoundTrip()
    {
        byte[] b = [0, 1, 2, 250, 251, 252, 253, 254, 255, 42];
        Assert.Equal(b, MegaCrypto.B64UrlDecode(MegaCrypto.B64UrlEncode(b)));
    }

    [Fact]
    public void EncryptKeyEcb_MatchesReference()
    {
        uint[] outp = MegaCrypto.EncryptKeyEcb(MegaCrypto.A32ToBytes([1, 2, 3, 4]), [5, 6, 7, 8]);
        Assert.Equal<uint[]>([1787807352, 4145219018, 181160935, 1477804820], outp);
    }

    [Fact]
    public void EncryptDecryptKeyEcb_RoundTrip()
    {
        byte[] key = MegaCrypto.A32ToBytes([9, 8, 7, 6]);
        uint[] data = [11, 22, 33, 44];
        Assert.Equal(data, MegaCrypto.DecryptKeyEcb(key, MegaCrypto.EncryptKeyEcb(key, data)));
    }

    [Fact]
    public void EncryptAttr_MatchesReference()
    {
        byte[] enc = MegaCrypto.EncryptAttr(new { n = "test.bin" }, [0x11111111, 0x22222222, 0x33333333, 0x44444444]);
        Assert.Equal("UvWZ3BmLkfBPgKBIKO9ntNN_Gat6SnS1H4lzeLvzXlM", MegaCrypto.B64UrlEncode(enc));
    }

    [Fact]
    public void EncryptChunkAndMac_AtOffsetZero_MatchesReference()
    {
        byte[] data = Range(70);
        (byte[] ct, uint[] mac) = MegaCrypto.EncryptChunkAndMac(data, [1, 2, 3, 4, 5, 6], 0);

        Assert.Equal(
            "19adae05337c36251506f3aa3e5997d9e643df08eed7aecdcb04250551ac3d82e353f82d62fd26a80e9ab2bdd423fc9cb0c94acc93a2f4aefbb5e59d33a76ff06ad234dd578e",
            Convert.ToHexString(ct).ToLowerInvariant());
        Assert.Equal<uint[]>([4090815870, 2991489487, 3661374477, 706610695], mac);

        // CTR is symmetric: re-running over the ciphertext recovers the plaintext.
        (byte[] back, _) = MegaCrypto.EncryptChunkAndMac(ct, [1, 2, 3, 4, 5, 6], 0);
        Assert.Equal(data, back);
    }

    [Fact]
    public void EncryptChunkAndMac_AtNonZeroOffset_MatchesReference()
    {
        (byte[] ct, uint[] mac) = MegaCrypto.EncryptChunkAndMac(Range(32), [1, 2, 3, 4, 5, 6], 1048576);
        Assert.Equal("ac9ff704c5ec8a040db06b13dcacf7b3afc37dfee92556bb5af5e8f5c581275e", Convert.ToHexString(ct).ToLowerInvariant());
        Assert.Equal<uint[]>([57712274, 2423721587, 2480841918, 980066082], mac);
    }

    [Fact]
    public void EncryptChunkAndMac_EmptyChunk_MacIsNonceTwice()
    {
        (byte[] ct, uint[] mac) = MegaCrypto.EncryptChunkAndMac([], [1, 2, 3, 4, 5, 6], 0);
        Assert.Empty(ct);
        Assert.Equal<uint[]>([5, 6, 5, 6], mac); // nonce = ulKey[4..6] = [5,6]; MAC = nonce||nonce
    }

    [Fact]
    public void CondenseMacs_And_BuildFileKey_MatchReference()
    {
        uint[] ulKey = [1, 2, 3, 4, 5, 6];
        uint[] mac1 = [4090815870, 2991489487, 3661374477, 706610695];
        uint[] mac2 = [57712274, 2423721587, 2480841918, 980066082];

        uint[] cond = MegaCrypto.CondenseMacs([mac1, mac2], ulKey);
        Assert.Equal<uint[]>([3387419069, 1761720533, 209517649, 1796001387], cond);

        uint[] fileKey = MegaCrypto.BuildFileKey(ulKey, cond);
        Assert.Equal<uint[]>([4, 4, 2699448683, 1735406142, 5, 6, 2699448680, 1735406138], fileKey);
        Assert.Equal("AAAABAAAAASg5lFrZ3AyPgAAAAUAAAAGoOZRaGdwMjo", MegaCrypto.A32ToB64(fileKey));
    }

    [Fact]
    public void Crc32b_MatchesReference_AndChains()
    {
        Assert.Equal(907060870u, MegaCrypto.Crc32b("hello"u8));
        // Chaining: Crc32b(b, Crc32b(a)) == Crc32b(a+b).
        Assert.Equal(4192936109u, MegaCrypto.Crc32b("world"u8, MegaCrypto.Crc32b("hello"u8)));
    }

    [Theory]
    // size -> first chunk offsets (from MEGA's chunkmap) + the empty-tail flag.
    [InlineData(70, false)]            // single short chunk, not a boundary
    [InlineData(131072, true)]         // exactly the first chunk boundary → needs empty tail
    [InlineData(0, true)]              // empty file → empty tail frame
    public void IterChunks_EmptyTailFlag(long size, bool expectEmptyTail)
    {
        (_, bool needEmptyTail) = MegaCrypto.IterChunks(size);
        Assert.Equal(expectEmptyTail, needEmptyTail);
    }

    [Fact]
    public void IterChunks_OffsetsFollowChunkmap()
    {
        (List<(long Offset, int Length)> chunks, _) = MegaCrypto.IterChunks(5_000_000);
        long[] expectedStarts = [0, 131072, 393216, 786432, 1310720, 1966080, 2752512, 3670016, 4718592];
        for (int i = 0; i < expectedStarts.Length; i++)
        {
            Assert.Equal(expectedStarts[i], chunks[i].Offset);
        }
    }

    private static byte[] Range(int n)
    {
        byte[] b = new byte[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = (byte)i;
        }

        return b;
    }
}
