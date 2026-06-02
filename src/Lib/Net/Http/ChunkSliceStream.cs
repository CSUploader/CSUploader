// <copyright file="ChunkSliceStream.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// Read-only stream wrapper that exposes at most <c>SliceLength</c> bytes from a
/// position-anchored underlying stream. Used by the XFileSharing chunked-upload path
/// to serve one 80 MiB slice of a file as a single <see cref="HttpContent"/> body
/// without copying — the underlying <see cref="FileStream"/> stays open across all
/// chunks and its position advances naturally as each slice is consumed.
/// </summary>
/// <remarks>
/// <para>
/// <c>HttpContent</c> needs <see cref="Length"/> up front (for <c>Content-Length</c>)
/// and reads via async <see cref="ReadAsync(byte[], int, int, CancellationToken)"/>
/// to completion. This class implements both with a counter against
/// <see cref="SliceLength"/>: <see cref="Read(byte[], int, int)"/> caps each call so
/// the wrapped <see cref="HttpClient"/> stops reading at exactly <c>SliceLength</c>
/// bytes even though the file has more data after that point.
/// </para>
/// <para>
/// <see cref="Dispose(bool)"/> does <b>not</b> close the underlying stream — the
/// caller owns the <see cref="FileStream"/>'s lifetime and will dispose it after the
/// final chunk has been sent.
/// </para>
/// </remarks>
public sealed class ChunkSliceStream(Stream inner, long sliceLength) : Stream
{
    private readonly Stream _inner = inner;
    private long _bytesReadFromSlice;

    public long SliceLength { get; } = sliceLength;

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => SliceLength;

    public override long Position
    {
        get => _bytesReadFromSlice;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long remaining = SliceLength - _bytesReadFromSlice;
        if (remaining <= 0)
        {
            return 0;
        }

        int allowed = (int)Math.Min(count, remaining);
        int read = _inner.Read(buffer, offset, allowed);
        _bytesReadFromSlice += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        long remaining = SliceLength - _bytesReadFromSlice;
        if (remaining <= 0)
        {
            return 0;
        }

        int allowed = (int)Math.Min(buffer.Length, remaining);
        int read = await _inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
        _bytesReadFromSlice += read;
        return read;
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // Deliberately don't dispose _inner — the caller's FileStream lives across multiple
        // slices and gets disposed once at the end of the upload loop.
        base.Dispose(disposing);
    }
}
