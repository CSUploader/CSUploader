// <copyright file="FileRetentionTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload.Pipeline;

namespace CSUploader.Tests.Upload.Pipeline;

public class FileRetentionTests
{
    [Fact]
    public void Unspecified_IsTheDefault_AndReportsNoExpiry()
    {
        FileRetention retention = default;

        Assert.Equal(FileRetentionBasis.Unspecified, retention.Basis);
        Assert.Equal(FileRetention.Unspecified, retention);
        Assert.Null(retention.Duration);
        Assert.False(retention.Expires);
    }

    [Fact]
    public void Permanent_HasNoDuration_ButIsNotUnspecified()
    {
        FileRetention retention = FileRetention.Permanent;

        Assert.Equal(FileRetentionBasis.Permanent, retention.Basis);
        Assert.Null(retention.Duration);
        Assert.False(retention.Expires);
        Assert.NotEqual(FileRetention.Unspecified, retention);
    }

    [Fact]
    public void DaysAfterUpload_CarriesBasisAndDuration()
    {
        FileRetention retention = FileRetention.DaysAfterUpload(3);

        Assert.Equal(FileRetentionBasis.AfterUpload, retention.Basis);
        Assert.Equal(TimeSpan.FromDays(3), retention.Duration);
        Assert.True(retention.Expires);
    }

    [Fact]
    public void DaysAfterLastDownload_CarriesBasisAndDuration()
    {
        FileRetention retention = FileRetention.DaysAfterLastDownload(15);

        Assert.Equal(FileRetentionBasis.AfterLastDownload, retention.Basis);
        Assert.Equal(TimeSpan.FromDays(15), retention.Duration);
        Assert.True(retention.Expires);
    }

    // The column's whole sort story in one place: unknown rows group (null), Permanent beats every
    // finite period, and finite periods order by actual length across bases.
    [Fact]
    public void SortKey_OrdersUnknownThenShortestToLongestThenPermanent()
    {
        Assert.Null(FileRetention.Unspecified.SortKey);
        Assert.Equal(double.PositiveInfinity, FileRetention.Permanent.SortKey);

        double hours24 = FileRetention.AfterUpload(TimeSpan.FromHours(24)).SortKey!.Value;
        double days15 = FileRetention.DaysAfterLastDownload(15).SortKey!.Value;
        double days100 = FileRetention.DaysAfterUpload(100).SortKey!.Value;

        Assert.True(hours24 < days15);
        Assert.True(days15 < days100);
        Assert.True(days100 < FileRetention.Permanent.SortKey!.Value);
    }
}
