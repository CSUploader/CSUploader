// <copyright file="ThrottledStream.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// A read-only stream wrapper that rate-limits reads to a caller-provided bytes/second value.
/// The limit is queried on each read via the <see cref="_getBytesPerSecond"/> delegate so it can
/// change live (e.g. when the user adjusts the speed limit while an upload is running).
/// Returning <c>null</c> or a non-positive value disables throttling.
/// </summary>
public class ThrottledStream(Stream inner, Func<long?> getBytesPerSecond) : Stream
{
    private readonly Stream _inner = inner;
    private readonly Func<long?> _getBytesPerSecond = getBytesPerSecond;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _bytesReadSinceReset;
    private long _windowStartMs;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int allowed = WaitForBudget(count);
        int read = _inner.Read(buffer, offset, allowed);
        _bytesReadSinceReset += read;
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int allowed = await WaitForBudgetAsync(count, cancellationToken);
        int read = await _inner.ReadAsync(buffer.AsMemory(offset, allowed), cancellationToken);
        _bytesReadSinceReset += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int allowed = await WaitForBudgetAsync(buffer.Length, cancellationToken);
        int read = await _inner.ReadAsync(buffer[..allowed], cancellationToken);
        _bytesReadSinceReset += read;
        return read;
    }

    private int WaitForBudget(int requestedBytes)
    {
        while (true)
        {
            int allowed = ComputeAllowedBytes(requestedBytes, out int sleepMs);
            if (allowed > 0)
            {
                return allowed;
            }

            Thread.Sleep(sleepMs);
        }
    }

    private async Task<int> WaitForBudgetAsync(int requestedBytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            int allowed = ComputeAllowedBytes(requestedBytes, out int sleepMs);
            if (allowed > 0)
            {
                return allowed;
            }

            await Task.Delay(sleepMs, cancellationToken);
        }
    }

    private int ComputeAllowedBytes(int requestedBytes, out int sleepMs)
    {
        sleepMs = 0;
        long? bps = _getBytesPerSecond();
        if (bps is null or <= 0)
        {
            _bytesReadSinceReset = 0;
            _windowStartMs = _clock.ElapsedMilliseconds;
            return requestedBytes;
        }

        long nowMs = _clock.ElapsedMilliseconds;
        long elapsedMs = nowMs - _windowStartMs;

        // Roll the 1-second window
        if (elapsedMs >= 1000)
        {
            _windowStartMs = nowMs;
            _bytesReadSinceReset = 0;
            elapsedMs = 0;
        }

        long remaining = bps.Value - _bytesReadSinceReset;
        if (remaining <= 0)
        {
            // Budget exhausted; wait until the window rolls over
            sleepMs = (int)Math.Max(1, 1000 - elapsedMs);
            return 0;
        }

        return (int)Math.Min(requestedBytes, remaining);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
