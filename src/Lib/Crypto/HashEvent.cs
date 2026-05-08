// <copyright file="HashEvent.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Crypto;

/// <summary>
/// Base type for all events emitted by <see cref="IHashingService.HashFileAsync"/>.
/// The terminal events are <see cref="HashCompleted"/> (success) and
/// <see cref="HashFailed"/> (error or file-not-found).
/// </summary>
public abstract record HashEvent;

/// <summary>
/// Emitted once at the start of a hash operation with the total byte count.
/// </summary>
public sealed record HashStarted(long TotalBytes) : HashEvent;

/// <summary>
/// Emitted periodically while the file is being read.
/// </summary>
public sealed record HashProgress(long BytesProcessed, long TotalBytes, double SpeedBytesPerSec) : HashEvent
{
    /// <summary>Gets the percentage of bytes processed (0–100).</summary>
    public double PercentComplete => TotalBytes > 0 ? (double)BytesProcessed / TotalBytes * 100.0 : 0.0;
}

/// <summary>
/// Terminal success event containing the hex-encoded hash and raw hash bytes.
/// </summary>
public sealed record HashCompleted(string HexHash, byte[] Hash) : HashEvent;

/// <summary>
/// Terminal failure event emitted when the hash cannot be computed.
/// </summary>
public sealed record HashFailed(string Reason, Exception? Exception) : HashEvent;
