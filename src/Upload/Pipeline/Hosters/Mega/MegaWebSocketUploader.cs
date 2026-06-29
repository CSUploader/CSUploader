// <copyright file="MegaWebSocketUploader.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Buffers.Binary;
using System.Net.WebSockets;

namespace CSUploader.Upload.Pipeline.Hosters.Mega;

/// <summary>Server message types on the MEGA upload WebSocket (from bdl4.js).</summary>
internal enum MegaWsMsgType
{
    ChunkAck = 1,
    ChunkAlready = 2,
    CrcFail = 3,
    Complete = 4,
    Shed = 5,
    Backoff = 6,
    ChunkAckAlt = 7,
}

/// <summary>A parsed server message: the type, the chunk offset (or backoff ms), and — for
/// <see cref="MegaWsMsgType.Complete"/> — the upload completion token.</summary>
internal readonly record struct MegaWsMessage(MegaWsMsgType Type, long Pos, byte[]? Token);

/// <summary>
/// Uploads a single file to a MEGA storage node over the binary WebSocket protocol the web client
/// (bdl4.js) and the transfer-it-cli reference speak: per chunk, a 20-byte little-endian header
/// (<c>fileno | pos | length | crc</c>) followed by the AES-CTR ciphertext frame; the server replies
/// with chunk acks and a final <c>COMPLETE</c> carrying the completion token. This v1 uses a single
/// connection (the reference fans out to 8 with reconnect-replay) — a transport failure propagates so
/// the pipeline's retry layer re-runs the whole upload against a fresh node, which never double-creates.
/// </summary>
internal static class MegaWebSocketUploader
{
    /// <summary>Builds the 20-byte chunk header: fileno, pos, length (all little-endian) and the
    /// CRC of <c>header[:16]</c> chained over the ciphertext.</summary>
    internal static byte[] BuildChunkHeader(uint fileno, long pos, int length, byte[] ciphertext)
    {
        byte[] header = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), fileno);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(4), (ulong)pos);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)length);
        uint crc = MegaCrypto.Crc32b(ciphertext, MegaCrypto.Crc32b(header.AsSpan(0, 16)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), crc);
        return header;
    }

    /// <summary>Parses + CRC-validates a server message (<c>body || crc32</c>). Throws on a CRC
    /// mismatch or a negative type code (server-signalled error).</summary>
    internal static MegaWsMessage ParseServerMessage(byte[] msg)
    {
        int bodyLen = msg.Length - 4;
        uint mcrc = BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(bodyLen));
        if (MegaCrypto.Crc32b(msg.AsSpan(0, bodyLen)) != mcrc)
        {
            throw new MegaApiException(0, "MEGA upload: server message CRC mismatch");
        }

        sbyte mtype = (sbyte)msg[12];
        if (mtype < 0)
        {
            throw new MegaApiException(0, $"MEGA upload: server signalled error type={mtype}");
        }

        long pos = (long)BinaryPrimitives.ReadUInt64LittleEndian(msg.AsSpan(4));
        var type = (MegaWsMsgType)mtype;
        byte[]? token = null;
        if (type == MegaWsMsgType.Complete)
        {
            int tlen = msg[13];
            token = msg[14..(14 + tlen)];
        }

        return new MegaWsMessage(type, pos, token);
    }

    /// <summary>
    /// Uploads <paramref name="filePath"/> to <c>wss://{host}/{uri}</c> and returns the completion
    /// token plus the per-chunk MACs ordered by offset (for <c>condense_macs</c>). Progress reports
    /// acked bytes. <paramref name="fileno"/> must be unique within the MEGA session.
    /// </summary>
    public static async Task<(byte[] Token, List<uint[]> Macs)> UploadAsync(
        string host,
        string uri,
        string filePath,
        uint[] ulKey,
        uint fileno,
        long size,
        Action<long, long>? progress,
        CancellationToken ct)
    {
        using ClientWebSocket ws = new();
        await ws.ConnectAsync(new Uri($"wss://{host}/{uri}"), ct).ConfigureAwait(false);

        (List<(long Offset, int Length)> chunks, bool needEmptyTail) = MegaCrypto.IterChunks(size);
        List<(long Pos, int Len)> work = [.. chunks.Select(c => (c.Offset, c.Length))];
        if (needEmptyTail)
        {
            work.Add((size, 0));
        }

        Dictionary<long, int> lengthByPos = work.ToDictionary(c => c.Pos, c => c.Len);
        SortedDictionary<long, uint[]> macsByOffset = [];
        byte[]? token = null;
        TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HashSet<long> ackedPos = [];
        long ackedBytes = 0;

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task ReceiveLoop()
        {
            byte[] buf = new byte[16384];
            using MemoryStream acc = new();
            while (!cts.IsCancellationRequested)
            {
                acc.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, cts.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    acc.Write(buf, 0, r.Count);
                }
                while (!r.EndOfMessage);

                byte[] msg = acc.ToArray();
                if (msg.Length < 14)
                {
                    continue;
                }

                MegaWsMessage m = ParseServerMessage(msg);
                switch (m.Type)
                {
                    case MegaWsMsgType.ChunkAck:
                    case MegaWsMsgType.ChunkAlready:
                    case MegaWsMsgType.ChunkAckAlt:
                        if (ackedPos.Add(m.Pos) && lengthByPos.TryGetValue(m.Pos, out int len))
                        {
                            long total = Interlocked.Add(ref ackedBytes, len);
                            progress?.Invoke(Math.Min(total, size), size);
                        }

                        break;
                    case MegaWsMsgType.Complete:
                        token = m.Token;
                        completed.TrySetResult();
                        return;
                    case MegaWsMsgType.Shed:
                        throw new MegaApiException(0, "MEGA upload: server requested reconnect (shed)");
                    case MegaWsMsgType.Backoff:
                        await Task.Delay((int)Math.Max(0, m.Pos), cts.Token).ConfigureAwait(false);
                        break;
                    case MegaWsMsgType.CrcFail:
                        throw new MegaApiException(0, $"MEGA upload: server reports chunk CRC fail at offset {m.Pos}");
                }
            }
        }

        Task recvTask = Task.Run(ReceiveLoop, cts.Token);

        try
        {
            await using FileStream fs = File.OpenRead(filePath);
            foreach ((long pos, int chunkLen) in work)
            {
                byte[] data = chunkLen == 0 ? [] : ReadExact(fs, pos, chunkLen);
                (byte[] cipher, uint[] mac) = MegaCrypto.EncryptChunkAndMac(data, ulKey, pos);
                macsByOffset[pos] = mac;

                byte[] header = BuildChunkHeader(fileno, pos, chunkLen, cipher);
                await ws.SendAsync(header, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
                if (cipher.Length > 0)
                {
                    await ws.SendAsync(cipher, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
                }

                if (completed.Task.IsCompleted || recvTask.IsFaulted)
                {
                    break;
                }
            }

            Task finished = await Task.WhenAny(completed.Task, recvTask).ConfigureAwait(false);
            if (finished == recvTask)
            {
                await recvTask.ConfigureAwait(false); // surface a receive-loop fault / unexpected close
                throw new MegaApiException(0, "MEGA upload: WebSocket closed before completion");
            }

            await completed.Task.ConfigureAwait(false);
        }
        finally
        {
            cts.Cancel();
            try
            {
                await recvTask.ConfigureAwait(false);
            }
            catch
            {
                // Receive loop unwinding on cancellation — already handled the meaningful outcome above.
            }
        }

        if (token is null)
        {
            throw new MegaApiException(0, "MEGA upload ended without a completion token");
        }

        return (token, [.. macsByOffset.Values]);
    }

    private static byte[] ReadExact(FileStream fs, long pos, int length)
    {
        fs.Seek(pos, SeekOrigin.Begin);
        byte[] data = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = fs.Read(data, read, length - read);
            if (n == 0)
            {
                throw new EndOfStreamException($"file ended early at offset {pos + read} (expected {length} bytes)");
            }

            read += n;
        }

        return data;
    }
}
