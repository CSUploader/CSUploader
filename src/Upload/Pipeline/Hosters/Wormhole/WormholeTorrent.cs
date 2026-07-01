// <copyright file="WormholeTorrent.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline.Hosters.Wormhole;

/// <summary>
/// Builds the single-file BitTorrent v1 <c>.torrent</c> wormhole.app stores (encrypted) for a room. The
/// torrent describes the <b>encrypted</b> stream — pieces are SHA-1 over the ece ciphertext, and
/// <c>length</c> is the ciphertext byte count — so the recipient can verify the Backblaze B2 blobs against
/// it before ece-decrypting. The torrent is <em>self-consistent</em> (the recipient uses this exact
/// <c>.torrent</c>, decrypted from the room), so the piece length is our free choice; only validity and a
/// stable infoHash matter.
/// </summary>
internal static class WormholeTorrent
{
    /// <summary>
    /// Bencodes a single-file torrent over the ciphertext and returns (torrent bytes, 20-byte infoHash).
    /// </summary>
    /// <param name="name">The file name (stored inside the <em>encrypted</em> torrent, so still private).</param>
    /// <param name="pieceLength">The torrent piece length (see <see cref="ChoosePieceLength"/>).</param>
    /// <param name="pieceHashes">The concatenated 20-byte SHA-1 of each piece of the CIPHERTEXT.</param>
    /// <param name="ciphertextLength">Total ece ciphertext length (what B2 stores / what pieces cover).</param>
    public static (byte[] Torrent, byte[] InfoHash) Build(string name, long pieceLength, byte[] pieceHashes, long ciphertextLength)
    {
        // Info-dict keys are emitted in sorted byte order by Bencode.Dict: length, name, piece length, pieces.
        byte[] info = Bencode.Dict(
            ("length", ciphertextLength),
            ("name", name),
            ("piece length", pieceLength),
            ("pieces", pieceHashes));

        byte[] infoHash = System.Security.Cryptography.SHA1.HashData(info);
        byte[] torrent = Bencode.Dict(("info", new Bencode.Raw(info)));
        return (torrent, infoHash);
    }

    /// <summary>Number of whole pieces for a ciphertext of <paramref name="ciphertextLength"/> at
    /// <paramref name="pieceLength"/> (the last piece may be short).</summary>
    public static long PieceCount(long ciphertextLength, long pieceLength)
        => ciphertextLength == 0 ? 0 : (ciphertextLength + pieceLength - 1) / pieceLength;

    /// <summary>Picks a torrent piece length: a power of two in [16 KiB, 4 MiB] targeting ≲1000 pieces.
    /// Because the torrent is self-consistent, the exact value only needs to be valid, not identical to
    /// webtorrent's calcPieceLength.</summary>
    public static long ChoosePieceLength(long ciphertextLength)
    {
        const long Min = 16 * 1024;
        const long Max = 4L * 1024 * 1024;
        long target = Math.Max(Min, ciphertextLength / 1000);
        long p = Min;
        while (p < target && p < Max)
        {
            p <<= 1;
        }

        return Math.Min(p, Max);
    }
}
