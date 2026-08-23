// <copyright file="UpdateServicePlanTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Update;
using Velopack;

namespace CSUploader.Tests.Lib.Update;

/// <summary>
/// Whether a size can be counted against the update's reported percentage.
/// <para>
/// <b>These pin an assumption about someone else's code</b>, so it is worth being exact about what
/// rides on it. Velopack decides between deltas and the full package by rules that are not in its
/// public API; <see cref="UpdateService.PlanDownload"/> asks the same question, because the
/// percentage is a byte fraction on one path and not on the other. Getting the answer wrong SHOWS or
/// HIDES the byte readout — it cannot produce a wrong figure, because the delta path has no figure
/// to get wrong. The rules were read from <c>UpdateManager.DownloadUpdatesAsync</c> in Velopack
/// 1.2.0, and nothing here can detect a future version changing them; that limit is real, and it is
/// why the failure mode was kept this mild.
/// </para>
/// </summary>
public class UpdateServicePlanTests
{
    /// <summary><c>Version</c> is left unset: the plan does not depend on delta order.</summary>
    private static VelopackAsset Asset(long size, VelopackAssetType type, string version) => new()
    {
        FileName = $"CSUploader-{version}-{type}.nupkg",
        Size = size,
        Type = type,
    };

    private static VelopackAsset Full(long size) => Asset(size, VelopackAssetType.Full, "9");

    private static VelopackAsset Delta(long size, string version) => Asset(size, VelopackAssetType.Delta, version);

    private static UpdateInfo Info(VelopackAsset full, VelopackAsset? baseRelease, params VelopackAsset[] deltas)
        => new(full, false, baseRelease, deltas);

    private static string Ordinal(int i) => i.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The ordinary case, and the one with no size: a base release plus a couple of small deltas
    /// means Velopack takes the delta path, where the percentage is not a byte fraction.
    /// </summary>
    [Fact]
    public void WithAnEligibleDeltaSet_ThereIsNoSizeToShow()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(
            Info(Full(90_000_000), Full(80_000_000), Delta(3_000_000, "1"), Delta(1_500_000, "2")));

        Assert.False(plan.IsKnown);
    }

    /// <summary>
    /// No base release means no delta attempt at all — Velopack requires
    /// <c>BaseRelease?.FileName</c> before it will even try, and goes straight to the full package.
    /// </summary>
    [Fact]
    public void WithNoBaseRelease_ThePlanIsTheFullPackage()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(
            Info(Full(90_000_000), null, Delta(3_000_000, "1")));

        Assert.Equal(90_000_000, plan.TotalBytes);
    }

    /// <summary>
    /// A base release that exists but carries no file name is not a base release as far as Velopack
    /// is concerned: it tests <c>BaseRelease?.FileName</c>, not <c>BaseRelease</c>.
    /// </summary>
    [Fact]
    public void WithABaseReleaseThatHasNoFileName_ThePlanIsTheFullPackage()
    {
        VelopackAsset nameless = new() { Size = 80_000_000, Type = VelopackAssetType.Full };

        UpdateDownloadPlan plan = UpdateService.PlanDownload(
            Info(Full(90_000_000), nameless, Delta(3_000_000, "1")));

        Assert.Equal(90_000_000, plan.TotalBytes);
    }

    /// <summary>
    /// Past ten deltas Velopack stops bothering with them. Eleven small ones still sum to far less
    /// than the full package, so only the COUNT rules this out.
    /// </summary>
    [Fact]
    public void WithTooManyDeltas_ThePlanIsTheFullPackage()
    {
        VelopackAsset[] eleven = [.. Enumerable.Range(1, 11).Select(i => Delta(100_000, Ordinal(i)))];

        UpdateDownloadPlan plan = UpdateService.PlanDownload(Info(Full(90_000_000), Full(80_000_000), eleven));

        Assert.Equal(90_000_000, plan.TotalBytes);
    }

    /// <summary>Exactly ten is still eligible — the rule is "more than ten", not "ten or more".</summary>
    [Fact]
    public void WithExactlyTenDeltas_TheDeltaPathIsStillTaken()
    {
        VelopackAsset[] ten = [.. Enumerable.Range(1, 10).Select(i => Delta(100_000, Ordinal(i)))];

        UpdateDownloadPlan plan = UpdateService.PlanDownload(Info(Full(90_000_000), Full(80_000_000), ten));

        Assert.False(plan.IsKnown);
    }

    /// <summary>
    /// Deltas that are not actually smaller are discarded: Velopack compares their sum against the
    /// full package, so a long chain of large deltas downloads the full one instead.
    /// </summary>
    [Fact]
    public void WhenTheDeltasAreNotSmaller_ThePlanIsTheFullPackage()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(
            Info(Full(5_000_000), Full(4_000_000), Delta(3_000_000, "1"), Delta(3_000_000, "2")));

        Assert.Equal(5_000_000, plan.TotalBytes);
    }

    /// <summary>Equal is still eligible — Velopack's test is <c>&lt;=</c>, so this takes the delta path.</summary>
    [Fact]
    public void WhenTheDeltasSumExactlyToTheFullSize_TheDeltaPathIsTaken()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(
            Info(Full(6_000_000), Full(4_000_000), Delta(3_000_000, "1"), Delta(3_000_000, "2")));

        Assert.False(plan.IsKnown);
    }

    [Fact]
    public void WithNoDeltas_ThePlanIsTheFullPackage()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(Info(Full(90_000_000), Full(80_000_000)));

        Assert.Equal(90_000_000, plan.TotalBytes);
    }

    /// <summary>
    /// A release advertising no size. Unknown hides the byte readout rather than showing a download
    /// of nothing.
    /// </summary>
    [Fact]
    public void WithNothingToGoOn_ThePlanIsUnknown()
    {
        Assert.False(UpdateService.PlanDownload(Info(Full(0), null)).IsKnown);
        Assert.False(UpdateService.PlanDownload(Info(Full(-1), null)).IsKnown);
    }

    /// <summary>
    /// Metadata that cannot be added up says nothing trustworthy about either path, so no size is
    /// claimed. Velopack's own <c>Sum</c> would throw here and be caught by its delta-fallback
    /// handler; this deliberately does NOT mirror that, because guessing which path results could
    /// put a wrong number on screen and declining cannot.
    /// </summary>
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(-1)]
    public void WithSizesThatCannotBeAddedUp_NoSizeIsClaimed(long deltaSize)
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(
            Info(Full(90_000_000), Full(80_000_000), Delta(deltaSize, "1"), Delta(deltaSize, "2")));

        Assert.False(plan.IsKnown);
    }
}
