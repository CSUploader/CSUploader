// <copyright file="ProgressStreamContent.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;

namespace CSUploader.Lib.Net.Http;

public class ProgressStreamContent(Stream content, Action<long, long> progress, CancellationToken cancellationToken) : StreamContent(content)
{
    private readonly Stream _content = content;
    private readonly Action<long, long> _progress = progress;
    private readonly CancellationToken _cancellationToken = cancellationToken;

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        byte[] buffer = new byte[81920];
        long totalBytesRead = 0;
        int bytesRead;

        while ((bytesRead = await _content.ReadAsync(buffer, _cancellationToken).ConfigureAwait(false)) != 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await stream.WriteAsync(buffer.AsMemory(0, bytesRead), _cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Writing to the request body stream failed mid-send (the server reset the
                // connection before we finished). The server received an incomplete request and
                // committed nothing, so this is a safe-to-retry body-transfer abort. Marking it
                // lets the retry layer distinguish it from a server verdict or a local read error.
                throw new UploadBodyTransferException(ex);
            }

            totalBytesRead += bytesRead;
            _progress(_content.Length, totalBytesRead);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _content.Length;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _content.Dispose();
        }

        base.Dispose(disposing);
    }
}
