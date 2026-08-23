// <copyright file="FileSliceReader.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Microsoft.Win32.SafeHandles;

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// Hands out independent readers over regions of one file, all backed by a single open handle.
/// <para>
/// This is the parallel counterpart to <see cref="ChunkSliceStream"/>, which deliberately shares one
/// caller-owned <see cref="FileStream"/> and rides its advancing position — correct and
/// allocation-free while parts are sent in order, and wrong the moment two are in flight, because
/// both would move the same position and each would receive a nondeterministic mixture of the file.
/// </para>
/// <para>
/// One ANCHOR handle is held for the whole transfer rather than opening per part, so the source file
/// cannot be swapped underneath a multi-part upload; the sequential path gets that property today
/// from holding one <see cref="FileStream"/> across its whole loop, and it must survive. Slices read
/// through <see cref="RandomAccess"/>, which is offset-addressed and shares no position, and are
/// re-openable because a retried part must re-send its bytes rather than EOF.
/// </para>
/// </summary>
public sealed class FileSliceReader : IDisposable
{
    private readonly SafeFileHandle _handle;

    public FileSliceReader(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
        FileLength = RandomAccess.GetLength(_handle);
    }

    public long FileLength { get; }

    public Stream OpenSlice(long fileOffset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        // Written as a subtraction, not `fileOffset + length > FileLength`: the addition can
        // overflow to negative and sail through the check.
        if (fileOffset > FileLength || length > FileLength - fileOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The slice extends past the end of the file.");
        }

        return new Slice(_handle, fileOffset, length);
    }

    public void Dispose() => _handle.Dispose();

    private sealed class Slice(SafeFileHandle handle, long fileOffset, long length) : Stream
    {
        // Captured explicitly. In the synchronous override the Stream.Read parameter named `offset`
        // is the BUFFER offset and shadows this one — writing `RandomAccess.Read(..., offset + _read)`
        // reads from the buffer offset as though it were a file position, so a slice starting at
        // 4096 read into buffer[20] returns file byte 20.
        private readonly long _fileOffset = fileOffset;
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        /// <summary>The SLICE's length — HttpContent uses it for Content-Length.</summary>
        public override long Length => length;

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int bufferOffset, int count)
        {
            // Validate FIRST. Computing `allowed` up front swallows a bad call: count == -1 makes
            // it negative, the guard below returns 0, and the caller sees a silent EOF where the
            // Stream contract requires ArgumentOutOfRangeException.
            ValidateBufferArguments(buffer, bufferOffset, count);

            int allowed = (int)Math.Min(count, length - _read);
            if (allowed <= 0)
            {
                return 0;
            }

            int n = RandomAccess.Read(handle, buffer.AsSpan(bufferOffset, allowed), _fileOffset + _read);
            _read += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int allowed = (int)Math.Min(buffer.Length, length - _read);
            if (allowed <= 0)
            {
                return 0;
            }

            int n = await RandomAccess.ReadAsync(handle, buffer[..allowed], _fileOffset + _read, cancellationToken).ConfigureAwait(false);
            _read += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
