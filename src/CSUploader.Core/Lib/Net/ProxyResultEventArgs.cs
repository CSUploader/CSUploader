// <copyright file="ProxyResultEventArgs.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Net;

/// <summary>
/// Live signal raised whenever a proxy's outcome is observed (manual test or upload).
/// Subscribers in the Connection Manager use it to keep the per-row status icon in sync.
/// </summary>
public sealed class ProxyResultEventArgs(int proxyId, bool success, string? message) : EventArgs
{
    public int ProxyId { get; } = proxyId;

    public bool Success { get; } = success;

    /// <summary>
    /// Short summary suitable for the per-row status text. Caller-supplied; pass null/empty
    /// to let the subscriber synthesize one.
    /// </summary>
    public string? Message { get; } = message;
}
