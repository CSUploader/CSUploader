// <copyright file="MegaWebSocketUploaderThrottleTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;
using CSUploader.Tests.TestSupport;
using CSUploader.Upload.Pipeline.Hosters.Mega;

namespace CSUploader.Tests.Upload.Pipeline.Hosters.Mega;

/// <summary>
/// MEGA and TransferIt obey the speed limit.
/// <para>
/// This path is the one exception to "throttling happens in <c>ThrottledStream</c>": it opens the
/// file itself and writes ciphertext straight to a <c>ClientWebSocket</c>, never touching
/// <c>HttpHandler</c>. The interesting behaviour is therefore how it paces its sends, so these tests
/// drive the real send loop against a fake socket rather than checking an accounting helper in
/// isolation.
/// </para>
/// </summary>
public class MegaWebSocketUploaderThrottleTests
{
    private sealed record Fragment(int Offset, int Count, bool EndOfMessage);

    /// <summary>Records what was sent, so a test can assert pacing and reassembly rather than
    /// merely that some bytes moved.</summary>
    private sealed class FakeSocket
    {
        public List<Fragment> Fragments { get; } = [];

        public List<byte> Received { get; } = [];

        public Task SendAsync(ArraySegment<byte> segment, bool endOfMessage, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Fragments.Add(new Fragment(segment.Offset, segment.Count, endOfMessage));
            Received.AddRange(segment.AsSpan().ToArray());
            return Task.CompletedTask;
        }
    }

    private static byte[] Cipher(int length)
        => [.. Enumerable.Range(0, length).Select(i => (byte)(i % 251))];

    /// <summary>
    /// The defect this shape exists to avoid. Charging a whole 1 MiB chunk and then sending it in
    /// one go waits ~10 s at 100 kB/s and then bursts a megabyte, defeating the bucket's deliberate
    /// 100 ms burst bound — and at low limits the pre-charge alone outlasts the 120 s idle watchdog,
    /// cancelling a healthy throttled upload.
    /// </summary>
    [Fact]
    public async Task SendChunkThrottledAsync_SplitsTheChunkIntoGrantSizedFragments()
    {
        ManualTimeProvider clock = new();
        SpeedLimiter limiter = new(() => 1_000_000, clock); // capacity = 100,000
        FakeSocket socket = new();
        byte[] cipher = Cipher(250_000);

        Task send = MegaWebSocketUploader.SendChunkThrottledAsync(
            socket.SendAsync, cipher, new SpeedBudget(() => limiter), () => { }, CancellationToken.None);

        for (int i = 0; i < 40 && !send.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(30);
        }

        await send;

        Assert.True(socket.Fragments.Count > 1, "the whole chunk went out in one burst");
        Assert.All(socket.Fragments, f => Assert.True(
            f.Count <= 100_000,
            $"a fragment of {f.Count} bytes exceeded the bucket's 100,000-byte capacity"));
    }

    [Fact]
    public async Task SendChunkThrottledAsync_SendsEveryByteOnceInOrder()
    {
        // Fragmenting must not corrupt the chunk: the server reassembles the fragments into one
        // message, so the concatenation has to equal the ciphertext exactly.
        ManualTimeProvider clock = new();
        SpeedLimiter limiter = new(() => 1_000_000, clock);
        FakeSocket socket = new();
        byte[] cipher = Cipher(250_000);

        Task send = MegaWebSocketUploader.SendChunkThrottledAsync(
            socket.SendAsync, cipher, new SpeedBudget(() => limiter), () => { }, CancellationToken.None);

        for (int i = 0; i < 40 && !send.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(30);
        }

        await send;

        Assert.Equal(cipher, socket.Received);
    }

    [Fact]
    public async Task SendChunkThrottledAsync_MarksEndOfMessageOnTheLastFragmentOnly()
    {
        // The chunk is ONE WebSocket message. Ending it early would deliver a truncated chunk.
        ManualTimeProvider clock = new();
        SpeedLimiter limiter = new(() => 1_000_000, clock);
        FakeSocket socket = new();

        Task send = MegaWebSocketUploader.SendChunkThrottledAsync(
            socket.SendAsync, Cipher(250_000), new SpeedBudget(() => limiter), () => { }, CancellationToken.None);

        for (int i = 0; i < 40 && !send.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(30);
        }

        await send;

        Assert.All(socket.Fragments[..^1], f => Assert.False(f.EndOfMessage));
        Assert.True(socket.Fragments[^1].EndOfMessage);
    }

    /// <summary>
    /// The watchdog is fed only by RECEIVED messages, so without an outbound signal a throttled
    /// upload looks idle and is cancelled after 120 s. Every fragment must report progress.
    /// </summary>
    [Fact]
    public async Task SendChunkThrottledAsync_ReportsProgressForEveryFragment()
    {
        ManualTimeProvider clock = new();
        SpeedLimiter limiter = new(() => 1_000_000, clock);
        FakeSocket socket = new();
        int signals = 0;

        Task send = MegaWebSocketUploader.SendChunkThrottledAsync(
            socket.SendAsync, Cipher(250_000), new SpeedBudget(() => limiter), () => signals++, CancellationToken.None);

        for (int i = 0; i < 40 && !send.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(30);
        }

        await send;

        Assert.Equal(socket.Fragments.Count, signals);
    }

    [Fact]
    public async Task SendChunkThrottledAsync_WithAnUnlimitedBudget_SendsInOneFragment()
    {
        // The unthrottled path must not pay for fragmentation it does not need.
        FakeSocket socket = new();

        await MegaWebSocketUploader.SendChunkThrottledAsync(
            socket.SendAsync, Cipher(1024 * 1024), SpeedBudget.Unlimited, () => { }, CancellationToken.None);

        Assert.Single(socket.Fragments);
        Assert.True(socket.Fragments[0].EndOfMessage);
    }

    [Fact]
    public async Task SendChunkThrottledAsync_WithAnEmptyChunk_SendsNothing()
    {
        // MEGA's chunk map emits an empty tail frame; its header carries the meaning, and there is
        // no ciphertext to pace.
        FakeSocket socket = new();

        await MegaWebSocketUploader.SendChunkThrottledAsync(
            socket.SendAsync, [], SpeedBudget.Unlimited, () => { }, CancellationToken.None);

        Assert.Empty(socket.Fragments);
    }

    [Fact]
    public async Task SendChunkThrottledAsync_HonoursCancellation()
    {
        SpeedLimiter limiter = new(() => 1_000, new ManualTimeProvider()); // frozen: never refills
        FakeSocket socket = new();
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MegaWebSocketUploader.SendChunkThrottledAsync(
                socket.SendAsync, Cipher(8192), new SpeedBudget(() => limiter), () => { }, cts.Token));
    }
}
