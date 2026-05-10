// <copyright file="HashingServiceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Security.Cryptography;
using CSUploader.Lib.Crypto;

namespace CSUploader.Tests.Lib.Crypto;

public class HashingServiceTests
{
    [Fact]
    public async Task HashFileAsync_KnownContent_YieldsHashStartedAndHashCompleted()
    {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        byte[] content = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(tempFile, content);

        try
        {
            var sut = new HashingService();

            // Act
            var events = new List<HashEvent>();
            await foreach (HashEvent e in sut.HashFileAsync(tempFile, HashAlgorithmName.MD5, CancellationToken.None))
            {
                events.Add(e);
            }

            // Assert
            Assert.Equal(2, events.Count);
            var started = Assert.IsType<HashStarted>(events[0]);
            Assert.Equal(content.Length, started.TotalBytes);

            var completed = Assert.IsType<HashCompleted>(events[1]);
            Assert.Equal(16, completed.Hash.Length);   // MD5 is always 16 bytes
            Assert.False(string.IsNullOrEmpty(completed.HexHash));
            Assert.Equal(32, completed.HexHash.Length); // 16 bytes * 2 hex chars
            Assert.Equal(completed.HexHash, Convert.ToHexString(completed.Hash).ToLowerInvariant());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task HashFileAsync_MultiChunkFile_YieldsProgressEvents()
    {
        // Use the test-only overload that disables the production throttle. Then the
        // service emits a progress event after every chunk (1 MiB) until the final one.
        // 4 MiB → 3 progress events + Started + Completed = 5 total.
        string tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        const int sizeBytes = 4 * 1024 * 1024;
        await File.WriteAllBytesAsync(tempFile, new byte[sizeBytes]);

        try
        {
            HashingService sut = new(TimeSpan.Zero);

            List<HashEvent> events = new();
            await foreach (HashEvent e in sut.HashFileAsync(tempFile, HashAlgorithmName.MD5, CancellationToken.None))
            {
                events.Add(e);
            }

            Assert.IsType<HashStarted>(events[0]);
            Assert.IsType<HashCompleted>(events[^1]);

            HashProgress[] progress = [.. events.OfType<HashProgress>()];
            Assert.NotEmpty(progress);
            Assert.All(progress, p => Assert.Equal(sizeBytes, p.TotalBytes));
            Assert.All(progress, p => Assert.True(p.BytesProcessed > 0));
            Assert.All(progress, p => Assert.True(p.PercentComplete is > 0 and < 100));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task HashFileAsync_NonExistentFile_YieldsHashFailed()
    {
        // Arrange
        string nonExistent = Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid() + ".bin");
        var sut = new HashingService();

        // Act
        var events = new List<HashEvent>();
        await foreach (HashEvent e in sut.HashFileAsync(nonExistent, HashAlgorithmName.MD5, CancellationToken.None))
        {
            events.Add(e);
        }

        // Assert
        Assert.Single(events);
        var failed = Assert.IsType<HashFailed>(events[0]);
        Assert.Contains(nonExistent, failed.Reason, StringComparison.Ordinal);
        Assert.Null(failed.Exception);
    }
}
