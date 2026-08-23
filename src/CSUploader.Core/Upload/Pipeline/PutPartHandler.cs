// <copyright file="PutPartHandler.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Test seam for one part upload.
/// <para>
/// Carries the BODY and a progress reporter as well as the addressing, because the assertions that
/// matter are about content and interleaving. A seam taking only (url, partNumber, offset, length)
/// can check the offsets a pipeline PASSED — proving its arithmetic ran — but never that the stream
/// actually delivers that region, which is exactly what a shared, position-advancing
/// <c>FileStream</c> gets wrong. It is also Task-based, so it can exercise overlap and cancellation
/// at all; the synchronous seam it replaced could do neither.
/// </para>
/// </summary>
internal delegate Task<HttpResponseSnapshot> PutPartHandler(
    string url,
    int partNumber,
    long fileOffset,
    long length,
    Stream body,
    Action<long> reportProgress,
    CancellationToken cancellationToken);
