// <copyright file="UpdateDownloadPlanTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Update;

namespace CSUploader.Tests.Lib.Update;

/// <summary>
/// How the updater's single percentage maps onto bytes — and when it cannot.
/// <para>
/// The naive reading, that percent is the byte fraction, holds for one package fetched whole and
/// only then. The delta path breaks it, and the plan's answer there is to have no size at all rather
/// than a derived one.
/// </para>
/// </summary>
public class UpdateDownloadPlanTests
{
    [Fact]
    public void AFullPackage_MapsPercentStraightOntoBytes()
    {
        UpdateDownloadPlan plan = UpdateDownloadPlan.Full(1000);

        Assert.True(plan.IsKnown);
        Assert.Equal(0, plan.BytesAt(0));
        Assert.Equal(250, plan.BytesAt(25));
        Assert.Equal(1000, plan.BytesAt(100));
    }

    [Fact]
    public void BytesNeverGoBackwardsAcrossTheWholeRange()
    {
        UpdateDownloadPlan plan = UpdateDownloadPlan.Full(1500);

        long previous = -1;
        for (int percent = 0; percent <= 100; percent++)
        {
            long now = plan.BytesAt(percent);
            Assert.True(now >= previous, $"bytes went backwards at {percent}%: {previous} → {now}");
            Assert.InRange(now, 0, plan.TotalBytes);
            previous = now;
        }
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(101)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void APercentageOutsideTheRange_StaysWithinTheTotal(int percent)
        => Assert.InRange(UpdateDownloadPlan.Full(1000).BytesAt(percent), 0, 1000);

    [Fact]
    public void AZeroOrNegativeSize_IsUnknown()
    {
        Assert.False(UpdateDownloadPlan.Full(0).IsKnown);
        Assert.False(UpdateDownloadPlan.Full(-1).IsKnown);
        Assert.Equal(0, UpdateDownloadPlan.Unknown.BytesAt(50));
        Assert.Equal(0, UpdateDownloadPlan.Unknown.TotalBytes);
    }

    /// <summary>
    /// A full package near the top of the range. The intermediate <c>percent / 100.0 * total</c> is
    /// a double, which cannot hold every long — at <see cref="long.MaxValue"/> it rounds UP, past
    /// what a long can represent. This passes because .NET saturates such conversions rather than
    /// leaving them undefined; the point of the test is that the guarantee is relied upon knowingly.
    /// </summary>
    [Fact]
    public void AnEnormousFullPackage_StaysWithinItsOwnTotal()
    {
        UpdateDownloadPlan plan = UpdateDownloadPlan.Full(long.MaxValue);

        Assert.InRange(plan.BytesAt(100), 0, long.MaxValue);
        Assert.InRange(plan.BytesAt(50), 0, long.MaxValue);
        Assert.True(plan.BytesAt(50) <= plan.BytesAt(100));
    }
}
