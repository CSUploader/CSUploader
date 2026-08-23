// <copyright file="UpdateDownloadPlanTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Update;

namespace CSUploader.Tests.Lib.Update;

/// <summary>
/// How the updater's single percentage maps onto bytes.
/// <para>
/// The naive reading — percent is the byte fraction — is right for a full package and wrong for
/// deltas, where the percentage runs through the downloads and then through a patch step that moves
/// nothing. These pin the real shape, read from Velopack 1.2.0's
/// <c>DownloadAndApplyDeltaUpdates</c>.
/// </para>
/// </summary>
public class UpdateDownloadPlanTests
{
    [Fact]
    public void AFullPackage_MapsPercentStraightOntoBytes()
    {
        UpdateDownloadPlan plan = UpdateDownloadPlan.Full(1000);

        Assert.Equal(0, plan.BytesAt(0));
        Assert.Equal(250, plan.BytesAt(25));
        Assert.Equal(1000, plan.BytesAt(100));
    }

    /// <summary>
    /// The delta phase ends at 70%. Everything above it is the patch process, which transfers
    /// nothing — so the bytes are complete there, and a naive reading would still be claiming 30% of
    /// the download was outstanding while the machine sat patching.
    /// </summary>
    [Fact]
    public void Deltas_AreCompleteAtSeventyPercent_NotAtAHundred()
    {
        UpdateDownloadPlan plan = UpdateDownloadPlan.Deltas([1000]);

        Assert.Equal(1000, plan.BytesAt(70));
        Assert.Equal(1000, plan.BytesAt(85));
        Assert.Equal(1000, plan.BytesAt(100));
    }

    /// <summary>With one delta the band IS the whole 0-70 range, so the curve is a straight line.</summary>
    [Fact]
    public void ASingleDelta_SpreadsLinearlyAcrossTheDownloadPhase()
    {
        UpdateDownloadPlan plan = UpdateDownloadPlan.Deltas([1400]);

        Assert.Equal(0, plan.BytesAt(0));
        Assert.Equal(700, plan.BytesAt(35)); // half of 70%
        Assert.Equal(1400, plan.BytesAt(70));
    }

    /// <summary>
    /// The one that a reasonable guess gets wrong. Velopack gives each delta an EQUAL share of the
    /// 0-70 range — it divides by the delta COUNT, not by their sizes — so a 900-byte delta followed
    /// by a 100-byte one is half done at 35%, not at 63%.
    /// </summary>
    [Fact]
    public void TwoDeltas_SplitTheRangeByCountRatherThanBySize()
    {
        UpdateDownloadPlan plan = UpdateDownloadPlan.Deltas([900, 100]);

        Assert.Equal(1000, plan.TotalBytes);

        // The band boundaries are the exact claims: two deltas split 0-70 into 0-35 and 35-70,
        // whatever their sizes. Size-weighting would put the handover at 63%, not 35%.
        Assert.Equal(0, plan.BytesAt(0));
        Assert.Equal(900, plan.BytesAt(35));   // the big delta done, the small one not started
        Assert.Equal(1000, plan.BytesAt(70));

        // ...and within each band it is proportional to THAT delta.
        Assert.InRange(plan.BytesAt(17), 400, 470);   // most of the way through 900
        Assert.InRange(plan.BytesAt(52), 940, 960);   // most of the way through the last 100
    }

    /// <summary>
    /// Order is the order Velopack downloads them in, and the curve depends on it: the same two
    /// sizes the other way round give a different byte count at the same percentage.
    /// </summary>
    [Fact]
    public void TheOrderOfTheDeltas_ChangesTheCurve()
    {
        Assert.Equal(900, UpdateDownloadPlan.Deltas([900, 100]).BytesAt(35));
        Assert.Equal(100, UpdateDownloadPlan.Deltas([100, 900]).BytesAt(35));
    }

    [Fact]
    public void BytesNeverGoBackwardsAcrossTheWholeRange()
    {
        UpdateDownloadPlan plan = UpdateDownloadPlan.Deltas([900, 100, 500]);

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
    {
        Assert.InRange(UpdateDownloadPlan.Deltas([900, 100]).BytesAt(percent), 0, 1000);
        Assert.InRange(UpdateDownloadPlan.Full(1000).BytesAt(percent), 0, 1000);
    }

    [Fact]
    public void AnEmptyOrZeroSizedPlan_IsUnknown()
    {
        Assert.False(UpdateDownloadPlan.Deltas([]).IsKnown);
        Assert.False(UpdateDownloadPlan.Deltas([0, 0]).IsKnown);
        Assert.False(UpdateDownloadPlan.Full(0).IsKnown);
        Assert.False(UpdateDownloadPlan.Full(-1).IsKnown);
        Assert.Equal(0, UpdateDownloadPlan.Unknown.BytesAt(50));
    }

    /// <summary>Sizes that cannot be summed are not sizes; wrapping would report a negative download.</summary>
    [Fact]
    public void SizesThatWouldOverflow_YieldUnknownRatherThanWrapping()
    {
        Assert.False(UpdateDownloadPlan.Deltas([long.MaxValue, long.MaxValue]).IsKnown);
        Assert.False(UpdateDownloadPlan.Deltas([-1]).IsKnown);
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
