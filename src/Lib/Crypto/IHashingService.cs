// <copyright file="IHashingService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Security.Cryptography;

namespace CSUploader.Lib.Crypto;

/// <summary>
/// Computes a cryptographic hash of a file and streams progress events to the caller.
/// <para>
/// Consumers iterate the returned <see cref="IAsyncEnumerable{T}"/> and handle each
/// <see cref="HashEvent"/> in turn. The stream always ends with one of two terminal events:
/// <list type="bullet">
///   <item><see cref="HashCompleted"/> — hashing succeeded; contains hex hash and raw bytes.</item>
///   <item><see cref="HashFailed"/> — hashing could not complete (file not found, I/O error, etc.).</item>
/// </list>
/// Cancellation is propagated via the <see cref="CancellationToken"/>; an
/// <see cref="OperationCanceledException"/> is thrown from the enumeration rather than
/// yielding a <see cref="HashFailed"/> event.
/// </para>
/// </summary>
public interface IHashingService
{
    /// <summary>
    /// Hashes the file at <paramref name="filePath"/> using the specified
    /// <paramref name="algorithm"/> and returns an async stream of <see cref="HashEvent"/> values.
    /// </summary>
    /// <param name="filePath">Absolute path to the file to hash.</param>
    /// <param name="algorithm">Hash algorithm to use (e.g. <see cref="HashAlgorithmName.MD5"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An <see cref="IAsyncEnumerable{T}"/> that yields a <see cref="HashStarted"/> event,
    /// optional <see cref="HashProgress"/> events, and finally either a
    /// <see cref="HashCompleted"/> or <see cref="HashFailed"/> terminal event.
    /// </returns>
    IAsyncEnumerable<HashEvent> HashFileAsync(string filePath, HashAlgorithmName algorithm, CancellationToken ct);
}
