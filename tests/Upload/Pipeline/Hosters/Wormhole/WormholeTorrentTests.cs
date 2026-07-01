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

    // ===== B2 blob layout: ONE object per torrent piece, piece length == B2 object size == 5,013,504
    // (verified against a real wormhole.app upload+download — the recipient fetches "<roomId>/<pieceIndex>"
    // directly from Backblaze, deriving the count from length / piece length, so the object split MUST equal
    // the torrent piece split or it requests blob indices that were never uploaded). =====

    [Fact]
    public void B2BlockSize_MatchesWormholeFixedPieceLength()
        => Assert.Equal(5_013_504L, WormholeTorrent.B2BlockSize); // 306 × 16384

    [Theory]
    [InlineData(2038, 1)]        // the real single-piece 2 KB capture: one object <room>/0
    [InlineData(12_003_149, 3)]  // the real 12 MB capture: 3 objects <room>/0..2
    [InlineData(5_013_504, 1)]   // exactly one full piece
    [InlineData(5_013_505, 2)]   // one byte over → two objects
    [InlineData(0, 0)]
    public void PieceCount_CountsB2ObjectsAtBlockSize(long ciphertextLength, long expected)
        => Assert.Equal(expected, WormholeTorrent.PieceCount(ciphertextLength, WormholeTorrent.B2BlockSize));

    [Fact]
    public void PieceSizeAt_SplitsCiphertextIntoBlockSizedObjects()
    {
        const long Total = 12_003_149; // the real 12 MB ciphertext → 3 pieces at the 5,013,504 block size
        long block = WormholeTorrent.B2BlockSize;
        Assert.Equal(5_013_504, WormholeTorrent.PieceSizeAt(Total, block, 0));
        Assert.Equal(5_013_504, WormholeTorrent.PieceSizeAt(Total, block, 1));
        Assert.Equal(1_976_141, WormholeTorrent.PieceSizeAt(Total, block, 2)); // last, short (matches the capture)
        Assert.Equal(0, WormholeTorrent.PieceSizeAt(Total, block, 3)); // past the end

        // The per-object sizes tile the whole ciphertext exactly — no bytes dropped or duplicated.
        long sum = 0;
        long count = WormholeTorrent.PieceCount(Total, block);
        for (long i = 0; i < count; i++)
        {
            sum += WormholeTorrent.PieceSizeAt(Total, block, i);
        }

        Assert.Equal(Total, sum);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
