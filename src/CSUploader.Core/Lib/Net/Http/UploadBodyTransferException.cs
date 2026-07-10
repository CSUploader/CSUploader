// <copyright file="UploadBodyTransferException.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// Thrown when an upload's request body could not be fully written to the network — i.e. the
/// connection aborted MID-SEND, before all bytes were transmitted. The server therefore received
/// an incomplete request and committed nothing, so re-sending the upload is SAFE (it cannot
/// double-create the file). The transport cause (IOException/SocketException) is the
/// InnerException. HttpClient wraps this in an HttpRequestException whose Message is
/// "Error while copying content to a stream.", so callers must walk the inner-exception chain
/// (use <see cref="IsInChain"/>) to detect it.
/// </summary>
public sealed class UploadBodyTransferException(Exception inner)
    : Exception("The upload request body was aborted before it was fully sent.", inner)
{
    /// <summary>True if <paramref name="ex"/> or any exception in its InnerException chain is a body-transfer abort.</summary>
    public static bool IsInChain(Exception? ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is UploadBodyTransferException)
            {
                return true;
            }
        }

        return false;
    }
}
