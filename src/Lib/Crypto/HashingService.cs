// <copyright file="HashingService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace CSUploader.Lib.Crypto;

/// <summary>
/// Default <see cref="IHashingService"/> implementation.
/// Uses <see cref="HashAlgorithm.ComputeHashAsync"/> for a clean, non-blocking hash computation.
/// </summary>
public sealed class HashingService : IHashingService
{
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
        yield return new HashStarted(info.Length);

        (byte[]? bytes, HashFailed? failure) = await ComputeAsync(filePath, algorithm, ct);

        if (failure is not null)
        {
            yield return failure;
            yield break;
        }

        string hex = Convert.ToHexString(bytes!).ToLowerInvariant();
        yield return new HashCompleted(hex, bytes!);
    }

    private static HashAlgorithm? CreateAlgorithm(HashAlgorithmName algorithm)
    {
        if (algorithm == HashAlgorithmName.MD5) return MD5.Create();
        if (algorithm == HashAlgorithmName.SHA1) return SHA1.Create();
        if (algorithm == HashAlgorithmName.SHA256) return SHA256.Create();
        if (algorithm == HashAlgorithmName.SHA384) return SHA384.Create();
        if (algorithm == HashAlgorithmName.SHA512) return SHA512.Create();
        return null;
    }

    private static async Task<(byte[]? Hash, HashFailed? Failure)> ComputeAsync(
        string filePath,
        HashAlgorithmName algorithm,
        CancellationToken ct)
    {
        try
        {
            using HashAlgorithm? hashAlg = CreateAlgorithm(algorithm);
            if (hashAlg is null)
            {
                return (null, new HashFailed($"Unknown hash algorithm: {algorithm.Name}", null));
            }

            await using FileStream fs = File.OpenRead(filePath);
            byte[] hash = await hashAlg.ComputeHashAsync(fs, ct);
            return (hash, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, new HashFailed(ex.Message, ex));
        }
    }
}
