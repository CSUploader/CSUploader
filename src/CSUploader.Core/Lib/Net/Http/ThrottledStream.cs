// <copyright file="ThrottledStream.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// A read-only stream wrapper that rate-limits reads against a <see cref="SpeedBudget"/>.
/// <para>
/// The budget is SHARED by every stream governed by the same limit, which is the whole point: this
/// class used to hold a <c>Func&lt;long?&gt;</c> returning a rate and enforce it against its own
/// private one-second window, so the scheduler's concurrent uploads were each allowed the full
/// limit and a user's 1 MB/s became N MB/s.
/// </para>
/// <para>
/// Every read reserves an allowance, moves at most that many bytes, and refunds the remainder in a
/// <c>finally</c> — covering the short read, the EOF probe and the cancelled read, all of which
/// would otherwise spend budget on bytes that never moved.
/// </para>
/// </summary>
public class ThrottledStream(Stream inner, SpeedBudget budget) : Stream
{
    private readonly Stream _inner = inner;
    private readonly SpeedBudget _budget = budget;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// The synchronous path, kept for interface completeness. No production caller uses it — all
    /// eight <c>HttpHandler</c> construction sites are upload bodies read asynchronously by
    /// <see cref="ProgressStreamContent"/> — and blocking here is not new behaviour.
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        // Validate BEFORE acquiring: a negative count would otherwise become a zero-byte grant and
        // a silent 0-return, where the Stream contract calls for ArgumentOutOfRangeException.
        ValidateBufferArguments(buffer, offset, count);

        SpeedReservation reservation = _budget.AcquireAsync(count, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        int read = 0;
        try
        {
            read = _inner.Read(buffer, offset, reservation.Bytes);
            return read;
        }
        finally
        {
            reservation.Refund(reservation.Bytes - read);
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        SpeedReservation reservation = await _budget.AcquireAsync(buffer.Length, cancellationToken).ConfigureAwait(false);
        int read = 0;
        try
        {
            read = await _inner.ReadAsync(buffer[..reservation.Bytes], cancellationToken).ConfigureAwait(false);
            return read;
        }
        finally
        {
            reservation.Refund(reservation.Bytes - read);
        }
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
