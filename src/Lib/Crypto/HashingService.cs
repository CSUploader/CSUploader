// <copyright file="HashingService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace CSUploader.Lib.Crypto;

/// <summary>
/// Default <see cref="IHashingService"/> implementation. Reads the file in 1 MiB chunks
/// and feeds them through <see cref="IncrementalHash"/>, yielding <see cref="HashProgress"/>
/// at most once every 250ms so the UI's Speed and Progress columns can update during long
/// hashing runs without flooding the channel for fast disks.
/// </summary>
public sealed class HashingService : IHashingService
{
    private const int ChunkSize = 1 << 20; // 1 MiB
    private static readonly TimeSpan DefaultProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimeSpan _progressInterval;

    public HashingService()
        : this(DefaultProgressInterval)
    {
    }

    /// <summary>
    /// Test-only overload: passes <see cref="TimeSpan.Zero"/> to emit progress on every
    /// chunk so unit tests don't depend on disk speed crossing the production throttle.
    /// </summary>
    public HashingService(TimeSpan progressInterval)
    {
        _progressInterval = progressInterval;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<HashEvent> HashFileAsync(
        string filePath,
        HashAlgorithmName algorithm,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            yield return new HashFailed($"File not found: {filePath}", null);
            yield break;
        }

        FileInfo info = new(filePath);
        long totalBytes = info.Length;
        yield return new HashStarted(totalBytes);

        IncrementalHash? hasher = TryCreateHasher(algorithm);
        if (hasher is null)
        {
            yield return new HashFailed($"Unknown hash algorithm: {algorithm.Name}", null);
            yield break;
        }

        using (hasher)
        {
            FileStream? fs = null;
            HashFailed? openFailure = null;
            try
            {
                fs = File.OpenRead(filePath);
            }
            catch (Exception ex)
            {
                openFailure = new HashFailed(ex.Message, ex);
            }

            if (openFailure is not null)
            {
                yield return openFailure;
                yield break;
            }

            using (fs!)
            {
                byte[] buffer = new byte[ChunkSize];
                long bytesProcessed = 0;
                DateTime startedAt = DateTime.UtcNow;
                DateTime lastProgress = startedAt;

                while (true)
                {
                    int read = 0;
                    HashFailed? readFailure = null;
                    try
                    {
                        read = await fs.ReadAsync(buffer.AsMemory(0, ChunkSize), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        readFailure = new HashFailed(ex.Message, ex);
                    }

                    if (readFailure is not null)
                    {
                        yield return readFailure;
                        yield break;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    hasher.AppendData(buffer, 0, read);
                    bytesProcessed += read;

                    // Skip the emit on the chunk that finishes the file so HashCompleted
                    // is the next event instead of a redundant 100% progress.
                    DateTime now = DateTime.UtcNow;
                    if (bytesProcessed < totalBytes && (now - lastProgress) >= _progressInterval)
                    {
                        double elapsed = (now - startedAt).TotalSeconds;
                        double speed = elapsed > 0 ? bytesProcessed / elapsed : 0.0;
                        yield return new HashProgress(bytesProcessed, totalBytes, speed);
                        lastProgress = now;
                    }
                }

                byte[] finalHash = hasher.GetHashAndReset();
                string hex = Convert.ToHexString(finalHash).ToLowerInvariant();
                yield return new HashCompleted(hex, finalHash);
            }
        }
    }

    private static IncrementalHash? TryCreateHasher(HashAlgorithmName algorithm)
    {
        try
        {
            return IncrementalHash.CreateHash(algorithm);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
