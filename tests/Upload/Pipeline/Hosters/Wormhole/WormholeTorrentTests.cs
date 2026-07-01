// <copyright file="WormholeTorrentTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Security.Cryptography;
using CSUploader.Upload.Pipeline.Hosters.Wormhole;

namespace CSUploader.Tests.Upload.Pipeline.Hosters.Wormhole;

/// <summary>
/// KATs for the bencode encoder + torrent builder, verified against the reference <c>bencode</c> npm
/// library, and the B2 blob-layout math verified against the real 5 MB capture (blob0 5,013,504 + blob1
/// 213,019 = 5,226,523 total).
/// </summary>
public class WormholeTorrentTests
{
    // pieces = SHA1("piece-0") || SHA1("piece-1") — matches the KAT generator.
    private static readonly byte[] Pieces = [.. SHA1.HashData("piece-0"u8), .. SHA1.HashData("piece-1"u8)];

    [Fact]
    public void Bencode_Dict_SortsKeysAndEncodesIntAndString()
    {
        // {foo:42, bar:"spam"} → sorted → d3:bar4:spam3:fooi42ee
        byte[] enc = Bencode.Dict(("foo", 42L), ("bar", "spam"));
        Assert.Equal("64333a626172343a7370616d333a666f6f6934326565", Hex(enc));
    }

    [Fact]
    public void Bencode_InfoDict_MatchesReference()
    {
        // Info dict over the "ciphertext": keys emitted sorted (length, name, piece length, pieces).
        byte[] info = Bencode.Dict(
            ("length", 200000L),
            ("name", "secret.bin"),
            ("piece length", 16384L),
            ("pieces", Pieces));

        Assert.Equal(
            "64363a6c656e6774686932303030303065343a6e616d6531303a7365637265742e62696e31323a7069656365206c656e67746869313633383465363a70696563657334303a1cd0428ddebfd0c58659d721def290efac985346983b5dc394b6243518e693fb62aa36141e19eb0465",
            Hex(info));
        Assert.Equal("8d1c2115567024f3ae4afec96f06874a955947f3", Hex(SHA1.HashData(info)));
    }

    [Fact]
    public void Torrent_Build_MatchesReferenceBytesAndInfoHash()
    {
        (byte[] torrent, byte[] infoHash) = WormholeTorrent.Build("secret.bin", pieceLength: 16384, pieceHashes: Pieces, ciphertextLength: 200000);

        Assert.Equal("8d1c2115567024f3ae4afec96f06874a955947f3", Hex(infoHash));
        Assert.Equal(
            "64343a696e666f64363a6c656e6774686932303030303065343a6e616d6531303a7365637265742e62696e31323a7069656365206c656e67746869313633383465363a70696563657334303a1cd0428ddebfd0c58659d721def290efac985346983b5dc394b6243518e693fb62aa36141e19eb046565",
            Hex(torrent));
    }

    // ===== B2 blob layout (verified against the real capture) =====

    [Fact]
    public void B2BlobSize_Is5013504()
        => Assert.Equal(5_013_504L, WormholeCrypto.B2BlobSize);

    [Theory]
    [InlineData(5_226_523, 2)] // the real 5 MB capture: 5,013,504 + 213,019
    [InlineData(5_013_504, 1)] // exactly one blob
    [InlineData(5_013_505, 2)] // one byte over
    [InlineData(100, 1)]
    public void BlobCount_MatchesLayout(long ciphertextLength, int expected)
        => Assert.Equal(expected, WormholeCrypto.BlobCount(ciphertextLength));

    [Fact]
    public void BlobSizeAt_SplitsLikeTheCapture()
    {
        const long Total = 5_226_523; // blob0 + blob1 from the capture
        Assert.Equal(5_013_504, WormholeCrypto.BlobSizeAt(Total, 0));
        Assert.Equal(213_019, WormholeCrypto.BlobSizeAt(Total, 1));
        Assert.Equal(0, WormholeCrypto.BlobSizeAt(Total, 2)); // past the end
    }

    [Theory]
    [InlineData(5_226_523)]
    [InlineData(16_384)]
    [InlineData(50_000_000)]
    public void ChoosePieceLength_IsValidPowerOfTwoInRange(long ciphertextLength)
    {
        long p = WormholeTorrent.ChoosePieceLength(ciphertextLength);
        Assert.InRange(p, 16 * 1024, 4L * 1024 * 1024);
        Assert.Equal(0, p & (p - 1)); // power of two
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
