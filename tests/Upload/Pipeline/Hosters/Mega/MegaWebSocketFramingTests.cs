// <copyright file="MegaWebSocketFramingTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Buffers.Binary;
using CSUploader.Upload.Pipeline.Hosters.Mega;

namespace CSUploader.Tests.Upload.Pipeline.Hosters.Mega;

/// <summary>
/// Known-answer tests for the MEGA upload WebSocket framing (<see cref="MegaWebSocketUploader"/>).
/// The header bytes and the COMPLETE message were produced by the Python reference's upload framing
/// with the same fixed inputs, so a match means our binary protocol is wire-compatible. The
/// connect/send/receive loop itself is exercised by the Stage-4 live upload.
/// </summary>
public class MegaWebSocketFramingTests
{
    // ciphertext of bytes(range(70)) under ulKey [1..6] at offset 0 — the Stage-1 chunk KAT.
    private const string Chunk1Hex =
        "19adae05337c36251506f3aa3e5997d9e643df08eed7aecdcb04250551ac3d82e353f82d62fd26a80e9ab2bdd423fc9cb0c94acc93a2f4aefbb5e59d33a76ff06ad234dd578e";

    [Fact]
    public void BuildChunkHeader_MatchesReference()
    {
        byte[] header = MegaWebSocketUploader.BuildChunkHeader(1, 0, 70, Convert.FromHexString(Chunk1Hex));
        Assert.Equal("010000000000000000000000460000007d9d3917", Convert.ToHexString(header).ToLowerInvariant());
    }

    [Fact]
    public void BuildChunkHeader_EmptyTail_MatchesReference()
    {
        byte[] header = MegaWebSocketUploader.BuildChunkHeader(2, 1048576, 0, []);
        Assert.Equal("02000000000010000000000000000000deefb54c", Convert.ToHexString(header).ToLowerInvariant());
    }

    [Fact]
    public void ParseServerMessage_Complete_ReturnsTypeAndToken()
    {
        byte[] msg = Convert.FromHexString(
            "0000000000000000000000000424000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122231184dba1");

        MegaWsMessage m = MegaWebSocketUploader.ParseServerMessage(msg);

        Assert.Equal(MegaWsMsgType.Complete, m.Type);
        Assert.Equal(0, m.Pos);
        Assert.Equal(Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20212223"), m.Token);
    }

    [Fact]
    public void ParseServerMessage_Ack_ReturnsTypeAndPos()
    {
        MegaWsMessage m = MegaWebSocketUploader.ParseServerMessage(Msg(1, 393216));
        Assert.Equal(MegaWsMsgType.ChunkAck, m.Type);
        Assert.Equal(393216, m.Pos);
        Assert.Null(m.Token);
    }

    [Fact]
    public void ParseServerMessage_BadCrc_Throws()
    {
        byte[] msg = Msg(1, 0);
        msg[5] ^= 0xFF; // corrupt the body → CRC no longer matches
        Assert.Throws<MegaApiException>(() => MegaWebSocketUploader.ParseServerMessage(msg));
    }

    [Fact]
    public void ParseServerMessage_NegativeType_Throws()
    {
        // type byte 0xFF == sbyte -1 → server-signalled error.
        Assert.Throws<MegaApiException>(() => MegaWebSocketUploader.ParseServerMessage(Msg(0xFF, 0)));
    }

    private static byte[] Msg(byte type, long pos)
    {
        byte[] body = new byte[13];
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4), (ulong)pos);
        body[12] = type;
        byte[] msg = new byte[17];
        body.CopyTo(msg, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(13), MegaCrypto.Crc32b(body));
        return msg;
    }
}
