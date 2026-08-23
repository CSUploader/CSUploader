// <copyright file="MegaUploadTransportJoinTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;
using System.Threading.Channels;
using CSUploader.Lib.Net.Http;
using CSUploader.Tests.TestSupport;
using CSUploader.Upload.Pipeline.Hosters.Mega;

namespace CSUploader.Tests.Upload.Pipeline.Hosters.Mega;

/// <summary>
/// The join the other MEGA suites cannot reach: that <c>UploadAsync</c> — the method the two
/// pipelines actually call — routes its chunk payloads through the throttled send.
/// <para>
/// <c>MegaWebSocketUploaderThrottleTests</c> drives <c>SendChunkThrottledAsync</c> directly, so
/// replacing the production call with a single raw <c>SendAsync</c> per chunk leaves every one of
/// its assertions green. That is exactly how MEGA and transfer.it came to ignore the user's speed
/// limit in the first place, and nothing pinned the way back. These tests drive the whole upload
/// against a fake socket instead, so the pacing is observed where it is actually wired.
/// </para>
/// </summary>
public class MegaUploadTransportJoinTests : IDisposable
{
    private const int FileBytes = 40 * 1024; // one MEGA chunk; the first is 128 KiB
    private const int LimitBytesPerSecond = 100_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"csu-mega-{Guid.NewGuid():N}");
    private readonly string _file;

    public MegaUploadTransportJoinTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "payload.bin");
        File.WriteAllBytes(_file, [.. Enumerable.Range(0, FileBytes).Select(i => (byte)(i % 251))]);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static readonly uint[] UlKey = [1, 2, 3, 4, 5, 6];

    private static readonly byte[] CompletionToken = [0xAA, 0xBB, 0xCC, 0xDD];

    /// <summary>
    /// A MEGA storage node, near enough: it reads the 20-byte headers, counts the ciphertext
    /// fragments that follow each one, acknowledges every chunk, and sends COMPLETE once the whole
    /// file has been acknowledged.
    /// <para>
    /// It asserts nothing itself. An exception thrown from inside <c>SendAsync</c> is swallowed by
    /// the uploader's own fault handling and would surface as a confusing upload failure rather than
    /// a failed assertion, so everything is recorded and checked by the test afterwards.
    /// </para>
    /// </summary>
    private sealed class FakeMegaSocket : IMegaSocket
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly Lock _sync = new();
        private readonly long _fileSize;

        private long _pendingPayload;
        private long _chunkPos;
        private int _chunkLength;
        private long _ackedBytes;

        public FakeMegaSocket(long fileSize) => _fileSize = fileSize;

        /// <summary>Sizes of the CIPHERTEXT frames, in order. Headers are excluded — they are sent
        /// whole and are not what the speed limit paces.</summary>
        public List<int> PayloadFragments { get; } = [];

        /// <summary>The <c>endOfMessage</c> flag of each ciphertext frame, so a test can check the
        /// fragmented chunk is still terminated as ONE web socket message.</summary>
        public List<bool> PayloadEnds { get; } = [];

        public int Headers { get; private set; }

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                if (_pendingPayload == 0)
                {
                    // Header: fileno | pos | length | crc, all little-endian.
                    Headers++;
                    _chunkPos = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(4));
                    _chunkLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12));
                    _pendingPayload = _chunkLength;

                    if (_pendingPayload == 0)
                    {
                        AckChunk();
                    }
                }
                else
                {
                    PayloadFragments.Add(buffer.Count);
                    PayloadEnds.Add(endOfMessage);
                    _pendingPayload -= buffer.Count;

                    if (_pendingPayload <= 0)
                    {
                        AckChunk();
                    }
                }
            }

            return Task.CompletedTask;
        }

        public async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            byte[] message = await _inbound.Reader.ReadAsync(cancellationToken);
            message.CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(message.Length, WebSocketMessageType.Binary, endOfMessage: true);
        }

        public void Dispose()
        {
        }

        private void AckChunk()
        {
            _inbound.Writer.TryWrite(Message(MegaWsMsgType.ChunkAck, _chunkPos));
            _ackedBytes += _chunkLength;

            if (_ackedBytes >= _fileSize)
            {
                _inbound.Writer.TryWrite(Message(MegaWsMsgType.Complete, 0, CompletionToken));
            }
        }
    }

    /// <summary>Builds the server frame <c>body || crc32(body)</c> the uploader's parser expects:
    /// the offset at [4..12), the type code at [12], and for COMPLETE a length-prefixed token
    /// from [13].</summary>
    private static byte[] Message(MegaWsMsgType type, long pos, byte[]? token = null)
    {
        int bodyLength = token is null ? 13 : 14 + token.Length;
        byte[] message = new byte[bodyLength + 4];
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(4), (ulong)pos);
        message[12] = (byte)type;

        if (token is not null)
        {
            message[13] = (byte)token.Length;
            token.CopyTo(message, 14);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            message.AsSpan(bodyLength), MegaCrypto.Crc32b(message.AsSpan(0, bodyLength)));
        return message;
    }

    /// <summary>
    /// Runs the upload against the fake, pumping the manual clock so the bucket refills without the
    /// test spending real seconds waiting for it.
    /// </summary>
    private async Task<(byte[] Token, FakeMegaSocket Socket)> RunAsync(SpeedBudget budget, ManualTimeProvider? clock)
    {
        FakeMegaSocket socket = new(FileBytes);

        Task<(byte[] Token, List<uint[]> Macs)> upload = MegaWebSocketUploader.UploadAsync(
            "node.example",
            "u/1",
            _file,
            UlKey,
            fileno: 1,
            FileBytes,
            progress: null,
            budget,
            CancellationToken.None,
            socketFactory: () => socket);

        for (int i = 0; i < 200 && !upload.IsCompleted; i++)
        {
            clock?.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }

        (byte[] token, _) = await upload.WaitAsync(TimeSpan.FromSeconds(30));
        return (token, socket);
    }

    /// <summary>
    /// The mutation that matters: replace the <c>SendChunkThrottledAsync</c> call in
    /// <c>UploadAsync</c> with a raw whole-chunk <c>SendAsync</c> and this fails — one 40 KiB frame
    /// instead of many bucket-sized ones. That regression is invisible to every other MEGA test.
    /// </summary>
    [Fact]
    public async Task UploadAsync_SendsItsChunksThroughTheSpeedLimit()
    {
        ManualTimeProvider clock = new();
        SpeedLimiter limiter = new(() => LimitBytesPerSecond, clock);

        (byte[] token, FakeMegaSocket socket) = await RunAsync(new SpeedBudget(() => limiter), clock);

        Assert.Equal(CompletionToken, token);
        Assert.Equal(1, socket.Headers);

        // The bucket holds a tenth of a second's worth, so no single grant — and therefore no single
        // frame — can exceed that. A whole-chunk send is 40 KiB against a 10 KB ceiling.
        Assert.True(socket.PayloadFragments.Count > 1, "the chunk went out in one frame: it is not being paced");
        Assert.All(socket.PayloadFragments, size => Assert.True(
            size <= LimitBytesPerSecond / 10,
            $"a {size.ToString(System.Globalization.CultureInfo.InvariantCulture)}-byte frame exceeds the bucket's burst bound"));
    }

    /// <summary>
    /// Fragmenting a chunk must not change what the server sees. Every ciphertext byte the header
    /// promised has to arrive, and the chunk must still terminate as exactly one web socket
    /// message — a stray <c>endOfMessage</c> mid-chunk would have the node parse a truncated frame.
    /// </summary>
    [Fact]
    public async Task Fragmenting_PreservesTheChunkAsOneMessage()
    {
        ManualTimeProvider clock = new();
        SpeedLimiter limiter = new(() => LimitBytesPerSecond, clock);

        (_, FakeMegaSocket socket) = await RunAsync(new SpeedBudget(() => limiter), clock);

        Assert.Equal(FileBytes, socket.PayloadFragments.Sum());
        Assert.Single(socket.PayloadEnds, end => end);
        Assert.True(socket.PayloadEnds[^1], "the LAST fragment must be the one that ends the message");
    }

    /// <summary>
    /// With no limit set there is nothing to pace, and the chunk should go out whole — the
    /// fragmentation is the cost of throttling, not a permanent change to how MEGA is fed.
    /// </summary>
    [Fact]
    public async Task WithNoSpeedLimit_TheChunkGoesOutWhole()
    {
        (byte[] token, FakeMegaSocket socket) = await RunAsync(SpeedBudget.Unlimited, clock: null);

        Assert.Equal(CompletionToken, token);
        Assert.Single(socket.PayloadFragments);
        Assert.Equal(FileBytes, socket.PayloadFragments[0]);
    }
}
