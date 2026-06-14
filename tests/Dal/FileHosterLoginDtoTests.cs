// <copyright file="FileHosterLoginDtoTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;

namespace CSUploader.Tests.Dal;

public class FileHosterLoginDtoTests
{
    [Fact]
    public void StorageAvailableBytes_BothQuotaAndUsedKnown_ReturnsDifference()
    {
        FileHosterLoginDto dto = new()
        {
            StorageUsedBytes = 695056440L,
            StorageQuotaBytes = 10737418240L,
        };

        Assert.Equal(10737418240L - 695056440L, dto.StorageAvailableBytes);
    }

    [Fact]
    public void StorageAvailableBytes_QuotaMissing_ReturnsNull()
    {
        // Hosters that report usage but no cap (none currently — but cover the shape).
        FileHosterLoginDto dto = new() { StorageUsedBytes = 100L };
        Assert.Null(dto.StorageAvailableBytes);
    }

    [Fact]
    public void StorageAvailableBytes_UsedMissing_ReturnsNull()
    {
        // Common XFS-family case: quota known, current usage not exposed.
        FileHosterLoginDto dto = new() { StorageQuotaBytes = 10737418240L };
        Assert.Null(dto.StorageAvailableBytes);
    }

    [Fact]
    public void StorageAvailableBytes_OverQuota_ClampsAtZero()
    {
        // FileBoom doesn't allow going over, but other K2S-family clones might lazily
        // sync; render the cell as "0 B" rather than a negative value.
        FileHosterLoginDto dto = new() { StorageUsedBytes = 100L, StorageQuotaBytes = 50L };
        Assert.Equal(0L, dto.StorageAvailableBytes);
    }

    [Fact]
    public void StorageAvailableBytes_BothNull_ReturnsNull()
    {
        FileHosterLoginDto dto = new();
        Assert.Null(dto.StorageAvailableBytes);
    }
}
