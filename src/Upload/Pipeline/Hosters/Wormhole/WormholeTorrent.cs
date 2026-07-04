// <copyright file="WormholeTorrent.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload.Pipeline.Hosters.Wormhole;

/// <summary>
/// Builds the single-file BitTorrent v1 <c>.torrent</c> wormhole.app stores (encrypted) for a room. The
/// torrent describes the <b>encrypted</b> stream — pieces are SHA-1 over the ece ciphertext, and
/// <c>length</c> is the ciphertext byte count — so the recipient can verify the Backblaze B2 blobs against
/// it before ece-decrypting. Critically, the piece length is <em>not</em> our free choice: wormhole stores
/// one B2 object per torrent piece (<c>&lt;roomId&gt;/&lt;pieceIndex&gt;</c>) and the recipient fetches
/// those blobs back <em>by piece index</em> derived from <c>length</c> / <c>piece length</c>, so the piece
/// length MUST equal the B2 object size (<see cref="B2BlockSize"/>) or the recipient requests block indices
/// that were never uploaded. (Verified against a real wormhole.app upload+download.)
/// </summary>
internal static class WormholeTorrent
{
    /// <summary>wormhole's fixed torrent piece length, which is also the Backblaze B2 object size:
    /// 5,013,504 = 306 × 16384. The ciphertext is sliced into pieces of this size (last piece short), each
    /// stored as one B2 object <c>&lt;roomId&gt;/&lt;i&gt;</c>; the recipient reads this value from the
    /// (decrypted) torrent and fetches exactly that many blobs. Confirmed from a live 12 MB round-trip:
    /// <c>piece length = 5013504</c>, 3 pieces, 3 B2 objects.</summary>
    public const long B2BlockSize = 306 * 16384; // 5,013,504

    /// <summary>
    /// Bencodes a single-file torrent over the ciphertext and returns (torrent bytes, 20-byte infoHash).
    /// </summary>
    /// <param name="name">The file name (stored inside the <em>encrypted</em> torrent, so still private).</param>
    /// <param name="pieceLength">The torrent piece length (always <see cref="B2BlockSize"/>).</param>
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
    /// <paramref name="pieceLength"/> (the last piece may be short). This is also the number of Backblaze B2
    /// objects the upload produces — wormhole stores one object per piece as <c>&lt;roomId&gt;/&lt;i&gt;</c>,
    /// and the recipient fetches those blobs back by piece index, so the object split MUST equal the piece
    /// split.</summary>
    public static long PieceCount(long ciphertextLength, long pieceLength)
        => ciphertextLength == 0 ? 0 : (ciphertextLength + pieceLength - 1) / pieceLength;

    /// <summary>Byte length of piece <paramref name="index"/> (0-based) for a ciphertext of the given
    /// length — <paramref name="pieceLength"/> for every piece but the last, which holds the remainder.
    /// This is the size of B2 object <c>&lt;roomId&gt;/&lt;index&gt;</c>.</summary>
    public static int PieceSizeAt(long ciphertextLength, long pieceLength, long index)
    {
        long start = index * pieceLength;
        return (int)Math.Clamp(ciphertextLength - start, 0, pieceLength);
    }

}
