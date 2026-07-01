// <copyright file="Bencode.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text;

namespace CSUploader.Upload.Pipeline.Hosters.Wormhole;

/// <summary>
/// Minimal BitTorrent bencode <em>encoder</em> — just enough to build a single-file <c>.torrent</c> for
/// wormhole.app (integers, byte strings, and dictionaries). Dictionary keys are emitted in ascending raw
/// byte order as bencode requires, so the output is canonical and its SHA-1 (the infoHash) is stable.
/// Verified byte-for-byte against the reference <c>bencode</c> npm library.
/// </summary>
internal static class Bencode
{
    /// <summary>Wraps already-bencoded bytes so they're embedded verbatim (e.g. a nested info dict) rather
    /// than re-encoded as a byte string.</summary>
    public sealed record Raw(byte[] Bytes);

    /// <summary>Encodes a dictionary from the given entries (keys sorted by raw bytes). Each value may be a
    /// <see cref="long"/>/<see cref="int"/> (integer), a <see cref="string"/> or <see cref="byte"/>[]
    /// (byte string), or a <see cref="Raw"/> (verbatim bencode).</summary>
    public static byte[] Dict(params (string Key, object Value)[] entries)
    {
        using MemoryStream ms = new();
        ms.WriteByte((byte)'d');
        foreach ((string key, object value) in entries.OrderBy(e => Encoding.UTF8.GetBytes(e.Key), ByteArrayComparer.Instance))
        {
            WriteByteString(ms, Encoding.UTF8.GetBytes(key));
            WriteValue(ms, value);
        }

        ms.WriteByte((byte)'e');
        return ms.ToArray();
    }

    private static void WriteValue(Stream s, object value)
    {
        switch (value)
        {
            case long l:
                WriteInteger(s, l);
                break;
            case int i:
                WriteInteger(s, i);
                break;
            case string str:
                WriteByteString(s, Encoding.UTF8.GetBytes(str));
                break;
            case byte[] bytes:
                WriteByteString(s, bytes);
                break;
            case Raw raw:
                s.Write(raw.Bytes);
                break;
            default:
                throw new ArgumentException($"bencode: unsupported value type {value.GetType()}");
        }
    }

    private static void WriteInteger(Stream s, long n)
    {
        byte[] b = Encoding.ASCII.GetBytes($"i{n}e");
        s.Write(b);
    }

    private static void WriteByteString(Stream s, byte[] bytes)
    {
        byte[] prefix = Encoding.ASCII.GetBytes($"{bytes.Length}:");
        s.Write(prefix);
        s.Write(bytes);
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
            => ((ReadOnlySpan<byte>)(x ?? [])).SequenceCompareTo(y ?? []);
    }
}
