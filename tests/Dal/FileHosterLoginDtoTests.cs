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

    [Fact]
    public void SetCheckStatus_RaisesPropertyChanged_ForCheckStatusAndStatusMessage()
    {
        // The Accounts grid relies on these notifications to re-render a row in place
        // (instead of replacing the DTO instance) on refresh/enable/disable.
        FileHosterLoginDto dto = new();
        List<string?> changed = [];
        dto.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        dto.SetCheckStatus(AccountCheckStatus.Valid, "ok");

        Assert.Contains(nameof(FileHosterLoginDto.CheckStatus), changed);
        Assert.Contains(nameof(FileHosterLoginDto.StatusMessage), changed);
        Assert.Equal(AccountCheckStatus.Valid, dto.CheckStatus);
        Assert.Equal("ok", dto.StatusMessage);
    }

    [Fact]
    public void StorageUsedBytes_Setter_RaisesPropertyChanged_ForUsedAndAvailable()
    {
        // StorageAvailableBytes is computed from StorageUsedBytes/StorageQuotaBytes, so the
        // "Available" column must be told to re-read when either operand changes.
        FileHosterLoginDto dto = new() { StorageQuotaBytes = 1000L };
        List<string?> changed = [];
        dto.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        dto.StorageUsedBytes = 250L;

        Assert.Contains(nameof(FileHosterLoginDto.StorageUsedBytes), changed);
        Assert.Contains(nameof(FileHosterLoginDto.StorageAvailableBytes), changed);
        Assert.Equal(750L, dto.StorageAvailableBytes);
    }
}
