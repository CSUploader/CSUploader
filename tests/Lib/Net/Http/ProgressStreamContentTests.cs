// <copyright file="ProgressStreamContentTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Tests.Lib.Net.Http;

/// <summary>
/// Verifies that <see cref="ProgressStreamContent"/> marks a mid-send network-write failure as a
/// <see cref="UploadBodyTransferException"/> (the safe-to-retry signal) and that
/// <see cref="UploadBodyTransferException.IsInChain"/> detects it even when HttpClient wraps it in
/// an <see cref="HttpRequestException"/>.
/// </summary>
public class ProgressStreamContentTests
{
    [Fact]
    public async Task SerializeToStreamAsync_DestinationWriteFails_ThrowsUploadBodyTransferException()
    {
        // A 1 KiB source; a destination stream whose WriteAsync always throws an IOException
        // (simulating a server reset mid-body).
        using var source = new MemoryStream(new byte[1024]);
        var content = new ProgressStreamContent(source, (_, _) => { }, CancellationToken.None);
        using var failingDest = new ThrowOnWriteStream(new IOException("connection reset", new SocketException(10054)));

        // CopyToAsync invokes the protected SerializeToStreamAsync.
        var ex = await Assert.ThrowsAsync<UploadBodyTransferException>(() => content.CopyToAsync(failingDest));
        Assert.True(UploadBodyTransferException.IsInChain(ex));
        Assert.IsType<IOException>(ex.InnerException);

        // The loop threw before exiting normally, so the body was NOT fully sent. This is the flag
        // the upload methods read to know the server committed nothing → safe to retry.
        Assert.False(content.BodyFullySent);
    }

    [Fact]
    public void IsInChain_WrappedInHttpRequestException_DetectsIt()
    {
        var wrapped = new HttpRequestException(
            "Error while copying content to a stream.",
            new UploadBodyTransferException(new IOException("reset")));
        Assert.True(UploadBodyTransferException.IsInChain(wrapped));
        Assert.False(UploadBodyTransferException.IsInChain(new HttpRequestException("plain 500")));
    }

    [Fact]
    public async Task SerializeToStreamAsync_SourceReadFails_IsNotClassifiedAsBodyTransferAbort()
    {
        // SAFETY INVARIANT: a LOCAL file read error is NOT a retryable body-transfer abort. If it
        // were marked as one, a partially-read local file could trigger a re-send even though the
        // first attempt may already have streamed (and the server committed) some bytes. The source
        // read (the loop condition) is left UNWRAPPED, so it must NOT carry the body-transfer signal.
        // (HttpContent.CopyToAsync wraps the raw IOException in an HttpRequest("Error while copying
        // content to a stream.") just as a real HttpClient send would — but the key invariant is
        // that no UploadBodyTransferException appears anywhere in the chain.)
        using var dest = new MemoryStream();
        using var failingSource = new ThrowOnReadStream(new IOException("disk read error"));
        var content = new ProgressStreamContent(failingSource, (_, _) => { }, CancellationToken.None);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => content.CopyToAsync(dest));
        Assert.False(UploadBodyTransferException.IsInChain(ex));

        // The underlying cause is the source-read IOException, proving the failure came from the
        // local read path (not a re-classified body-transfer abort).
        bool ioInChain = false;
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is IOException)
            {
                ioInChain = true;
            }
        }

        Assert.True(ioInChain);
    }

    [Fact]
    public async Task SerializeToStreamAsync_FullCopySucceeds_TransfersEverythingAndReportsProgress()
    {
        // POSITIVE path: a healthy copy must move every byte and not throw. The progress callback's
        // final transferred value must equal the source length so the UI shows a completed upload.
        var payload = new byte[1024];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        using var source = new MemoryStream(payload);
        using var dest = new MemoryStream();
        long lastTransferred = -1;
        var content = new ProgressStreamContent(source, (_, transferred) => lastTransferred = transferred, CancellationToken.None);

        await content.CopyToAsync(dest);

        Assert.Equal(source.Length, dest.Length);
        Assert.Equal(payload, dest.ToArray());
        Assert.Equal(source.Length, lastTransferred);

        // The copy loop ran to completion (ReadAsync returned 0), so every byte was written. This
        // is the ONLY path that sets the flag — a fault after this point (e.g. a lost response) is
        // therefore NOT mistaken for an incomplete body, preserving the never-double-create invariant.
        Assert.True(content.BodyFullySent);
    }

    /// <summary>
    /// A write-only sink whose <see cref="WriteAsync(System.ReadOnlyMemory{byte}, CancellationToken)"/>
    /// (and the legacy <c>byte[]</c> overload) always fault with the supplied exception, simulating a
    /// server RST mid-body. Everything else is a no-op.
    /// </summary>
    private sealed class ThrowOnWriteStream(Exception toThrow) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get => 0; set { } }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(toThrow);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException(toThrow);

        public override void Write(byte[] buffer, int offset, int count) => throw toThrow;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// A readable source whose <see cref="ReadAsync(System.Memory{byte}, CancellationToken)"/> (and the
    /// legacy <c>byte[]</c> overload) always fault with the supplied exception, simulating a local file
    /// read error. Mirrors <see cref="ThrowOnWriteStream"/>. Everything else is a no-op.
    /// </summary>
    private sealed class ThrowOnReadStream(Exception toThrow) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 0;

        public override long Position { get => 0; set { } }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(toThrow);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException<int>(toThrow);

        public override int Read(byte[] buffer, int offset, int count) => throw toThrow;

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
