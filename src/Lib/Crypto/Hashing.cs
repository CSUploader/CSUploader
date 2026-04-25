// <copyright file="Hashing.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Security.Cryptography;

namespace CSUploader.Lib.Crypto;

public class Hashing
{
    public event EventHandler<OperationProgressEventArgs>? HashingProgress;

    public event EventHandler<HashingFinishedEventArgs>? HashingFinished;

    public int BufferSize { get; set; } = 1048576;

    public async Task<byte[]> ComputeHashAsync(HashAlgorithm hashAlgorithm, Stream stream, PauseToken pauseToken = default, CancellationToken cancellationToken = default)
    {
        DateTime dateTimeStarted = DateTime.Now;
        long totalBytesRead = 0;
        byte[] buffer = new byte[BufferSize];

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await pauseToken.PauseIfRequestedAsync();

            totalBytesRead += bytesRead;

            hashAlgorithm.TransformBlock(buffer, 0, bytesRead, buffer, 0);

            FireHashingProgress(stream.Length, totalBytesRead, dateTimeStarted);
        }

        hashAlgorithm.TransformFinalBlock(buffer, 0, 0);

        FireHashingFinished(true, dateTimeStarted, hashAlgorithm.Hash ?? []);

        return hashAlgorithm.Hash ?? [];
    }

    protected virtual void FireHashingProgress(long size, long bytesProcessed, DateTime dateTimeStarted) => HashingProgress?.Invoke(this, new OperationProgressEventArgs(size, bytesProcessed, dateTimeStarted));

    protected virtual void FireHashingFinished(bool success, DateTime dateTimeStarted, byte[] hash) => HashingFinished?.Invoke(this, new HashingFinishedEventArgs(success, dateTimeStarted, hash));
}
