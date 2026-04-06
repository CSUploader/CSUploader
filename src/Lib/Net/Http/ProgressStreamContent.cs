// <copyright file="ProgressStreamContent.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net.Http;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public class ProgressStreamContent : StreamContent
{
    private readonly Stream content;
    private readonly Action<long, long> progress;
    private readonly CancellationToken cancellationToken;

    public ProgressStreamContent(Stream content, Action<long, long> progress, CancellationToken cancellationToken)
        : base(content)
    {
        this.content = content;
        this.progress = progress;
        this.cancellationToken = cancellationToken;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var buffer = new byte[81920];
        long totalBytesRead = 0;
        int bytesRead;

        while ((bytesRead = await content.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);

            totalBytesRead += bytesRead;
            progress(content.Length, totalBytesRead);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = content.Length;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            content.Dispose();
        }

        base.Dispose(disposing);
    }
}
