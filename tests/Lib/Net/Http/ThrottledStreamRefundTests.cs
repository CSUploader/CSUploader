// <copyright file="ThrottledStreamRefundTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;
using CSUploader.Tests.TestSupport;

namespace CSUploader.Tests.Lib.Net.Http;

/// <summary>
/// That <see cref="ThrottledStream"/> gives back what it did not move.
/// <para>
/// This needs its own tests because the wall-clock concurrency tests cannot catch a missing refund:
/// failing to refund only ever makes a transfer SLOWER, so it stays comfortably inside a
/// "no faster than the limit" assertion. And <c>SpeedLimiter.Refund</c> being correct in isolation
/// says nothing about whether the stream calls it. The cost is real —
/// <c>ProgressStreamContent</c> reads in 81,920-byte buffers until zero, so one unrefunded EOF
/// probe spends a whole bucket at a low limit and starves every sibling.
/// </para>
/// </summary>
public class ThrottledStreamRefundTests
{
    private const int Limit = 100_000;

    private static (ThrottledStream Stream, SpeedLimiter Limiter) Build(Stream inner, ManualTimeProvider clock)
    {
        SpeedLimiter limiter = new(() => Limit, clock);
        clock.Advance(TimeSpan.FromMilliseconds(100)); // fill the bucket to capacity: 10,000
        return (new ThrottledStream(inner, new SpeedBudget(() => limiter)), limiter);
    }

    /// <summary>How much of the bucket is left, without disturbing it further than the take.</summary>
    private static long Remaining(SpeedLimiter limiter)
    {
        long total = 0;
        while (true)
        {
            SpeedReservation r = limiter.TryAcquire(int.MaxValue);
            if (r.Bytes == 0)
            {
                return total;
            }

            total += r.Bytes;
        }
    }

    [Fact]
    public async Task AShortRead_RefundsTheBytesItDidNotMove()
    {
        // The inner stream has only 100 bytes but the caller asks for 8192. Without a refund the
        // bucket is charged 8192 for 100 bytes of actual transfer.
        ManualTimeProvider clock = new();
        (ThrottledStream stream, SpeedLimiter limiter) = Build(new MemoryStream(new byte[100]), clock);

        int read = await stream.ReadAsync(new byte[8192]);

        Assert.Equal(100, read);
        Assert.Equal(10_000 - 100, Remaining(limiter));
    }

    [Fact]
    public async Task AnEofProbe_CostsNothing()
    {
        // ProgressStreamContent reads until zero, so every upload ends with one of these.
        ManualTimeProvider clock = new();
        (ThrottledStream stream, SpeedLimiter limiter) = Build(new MemoryStream([]), clock);

        int read = await stream.ReadAsync(new byte[8192]);

        Assert.Equal(0, read);
        Assert.Equal(10_000, Remaining(limiter)); // untouched
    }

    [Fact]
    public async Task AnInnerStreamThatThrows_StillRefunds()
    {
        // The refund lives in a finally precisely for this: a mid-transfer transport fault must not
        // leave the shared bucket charged for bytes nobody sent.
        ManualTimeProvider clock = new();
        (ThrottledStream stream, SpeedLimiter limiter) = Build(new ThrowingStream(), clock);

        await Assert.ThrowsAsync<IOException>(async () => await stream.ReadAsync(new byte[8192]));

        Assert.Equal(10_000, Remaining(limiter));
    }

    [Fact]
    public async Task ACancelledRead_StillRefunds()
    {
        ManualTimeProvider clock = new();
        (ThrottledStream stream, SpeedLimiter limiter) = Build(new BlockingStream(), clock);
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await stream.ReadAsync(new byte[8192], cts.Token));

        Assert.Equal(10_000, Remaining(limiter));
    }

    [Fact]
    public async Task AFullRead_RefundsNothing()
    {
        // The other direction: a refund that gave back too much would let the bucket outrun its rate.
        ManualTimeProvider clock = new();
        (ThrottledStream stream, SpeedLimiter limiter) = Build(new MemoryStream(new byte[8192]), clock);

        int read = await stream.ReadAsync(new byte[8192]);

        Assert.Equal(8192, read);
        Assert.Equal(10_000 - 8192, Remaining(limiter));
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            => throw new IOException("the connection dropped mid-body");

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("the connection dropped mid-body");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
