// <copyright file="Hashing.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Security.Cryptography;

namespace CSUploader.Lib.Crypto;

public class Hashing
{
    public event EventHandler<HashingProgressEventArgs>? HashingProgress;

    public event EventHandler<HashingFinishedEventArgs>? HashingFinished;

    public int BufferSize { get; set; } = 1048576;

    public async Task<byte[]> ComputeHashAsync(HashAlgorithm hashAlgorithm, Stream stream, PauseToken pauseToken = default, CancellationToken cancellationToken = default)
    {
        DateTime dateTimeStarted = DateTime.Now;
        long totalBytesRead = 0;
        byte[] buffer = new byte[BufferSize];
        int bytesRead = await stream.ReadAsync(buffer, cancellationToken);
        while (bytesRead > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            await pauseToken.PauseIfRequestedAsync();

            hashAlgorithm.TransformBlock(buffer, 0, bytesRead, buffer, 0);

            FireHashingProgress(stream.Length, totalBytesRead, dateTimeStarted);

            bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            totalBytesRead += bytesRead;
        }

        hashAlgorithm.TransformFinalBlock(buffer, 0, bytesRead);

        FireHashingFinished(true, dateTimeStarted, hashAlgorithm.Hash ?? []);

        return hashAlgorithm.Hash ?? [];
    }

    protected virtual void FireHashingProgress(long size, long bytesProcessed, DateTime dateTimeStarted)
    {
        HashingProgress?.Invoke(this, new HashingProgressEventArgs(size, bytesProcessed, dateTimeStarted));
    }

    protected virtual void FireHashingFinished(bool success,  DateTime dateTimeStarted, byte[] hash)
    {
        HashingFinished?.Invoke(this, new HashingFinishedEventArgs(success, dateTimeStarted, hash));
    }
}
