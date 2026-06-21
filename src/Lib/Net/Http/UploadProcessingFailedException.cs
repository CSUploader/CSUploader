// <copyright file="UploadProcessingFailedException.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net.Http;

/// <summary>
/// Thrown by a hoster pipeline when the server ACCEPTED the uploaded bytes but its post-upload
/// processing reported the upload FAILED and created NO file (e.g. Alfafile / Rapidgator
/// upload_info state 3 "Fail" with an empty file). Because no file was committed, re-running the
/// whole upload cannot double-create — so the shared retry layer (AttemptRunner) treats this as a
/// safe-to-retry fault, distinct from a mid-send transport abort (UploadBodyTransferException).
/// The message carries the server's raw response so an exhausted retry is still diagnosable.
/// </summary>
public sealed class UploadProcessingFailedException(string message) : Exception(message)
{
    public static bool IsInChain(Exception? ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is UploadProcessingFailedException) return true;
        }
        return false;
    }
}
