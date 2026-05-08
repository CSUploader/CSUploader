// <copyright file="UploadEvent.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Upload.Pipeline;

/// <summary>
/// Base type for every event emitted by an upload attempt. Subscribers (PackageFile,
/// ProxyManager) pattern-match the concrete type to update state.
/// </summary>
public abstract record UploadEvent;

public sealed record ProxyPicked(ProxyChoice Proxy) : UploadEvent;

public sealed record HandlerBuilt(HttpHandler Handler) : UploadEvent;

public sealed record AuthStarted : UploadEvent;

public sealed record AuthSucceeded : UploadEvent;

public sealed record AuthFailed(string Reason) : UploadEvent;

public sealed record TransferStarted(long TotalBytes) : UploadEvent;

public sealed record TransferProgress(long BytesUploaded, long TotalBytes, double SpeedBytesPerSec) : UploadEvent
{
    public double PercentComplete => TotalBytes > 0 ? (double)BytesUploaded / TotalBytes * 100.0 : 0.0;
}

public sealed record TransferCompleted(string FileUrl) : UploadEvent;

public sealed record AttemptCancelled : UploadEvent;

public sealed record AttemptFailed(string Reason, Exception? Exception) : UploadEvent;

/// <summary>
/// Final terminal event for every attempt — emitted exactly once. ProxyManager listens
/// for this to update its connectivity icons; PackageFile uses it to flip terminal state.
/// </summary>
public sealed record AttemptCompleted(bool Success, int ProxyId, string? FileUrl) : UploadEvent;
