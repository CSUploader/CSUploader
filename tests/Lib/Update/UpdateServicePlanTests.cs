// <copyright file="UpdateServicePlanTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Update;
using Velopack;

namespace CSUploader.Tests.Lib.Update;

/// <summary>
/// Which package the update window's byte readout counts against.
/// <para>
/// <b>These pin an assumption about someone else's code.</b> Velopack decides between deltas and the
/// full package by rules that are not in its public API, and
/// <see cref="UpdateService.PlanDownload"/> mirrors them so the figures count against whatever will
/// actually be fetched. The rules below were read from <c>UpdateManager.DownloadUpdatesAsync</c> in
/// Velopack 1.2.0; if a future version changes them, these are the tests that should fail, and the
/// wrong answer they are protecting against is a byte count that is out by the ratio between a delta
/// and a full release — which on this app is roughly thirty to one.
/// </para>
/// </summary>
public class UpdateServicePlanTests
{
    /// <summary>
    /// <c>Version</c> is deliberately left unset: its type is not reachable from this project, and
    /// every assertion here is on the TOTAL, which does not depend on the order. The order does
    /// matter to the byte curve, and that half is pinned in <c>UpdateDownloadPlanTests</c> against
    /// plain sizes. What stays unpinned is the single <c>OrderBy(d =&gt; d.Version)</c> in
    /// <see cref="UpdateService.PlanDownload"/> that mirrors Velopack's own.
    /// </summary>
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

    /// <summary>The ordinary case: a base release and a couple of small deltas.</summary>
    [Fact]
    public void WithAnEligibleDeltaSet_ThePlanCountsTheDeltas()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(
            Info(Full(90_000_000), Full(80_000_000), Delta(3_000_000, "1"), Delta(1_500_000, "2")));

        Assert.Equal(4_500_000, plan.TotalBytes);
    }

    /// <summary>
    /// <b>No base release means no delta attempt at all.</b> Velopack requires
    /// <c>BaseRelease?.FileName</c> before it will even try, and goes straight to the full package —
    /// so a plan that counted the deltas here would be wrong from the first byte, with no failure
    /// and no fallback to correct it.
    /// </summary>
    [Fact]
    public void WithNoBaseRelease_ThePlanIsTheFullPackage()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(
            Info(Full(90_000_000), null, Delta(3_000_000, "1")));

        Assert.Equal(90_000_000, plan.TotalBytes);
    }

    /// <summary>
    /// Past ten deltas Velopack stops bothering with them. Eleven small ones still sum to less than
    /// the full package, so only the COUNT rules this out.
    /// </summary>
    [Fact]
    public void WithTooManyDeltas_ThePlanIsTheFullPackage()
    {
        VelopackAsset[] eleven = [.. Enumerable.Range(1, 11).Select(i => Delta(100_000, i.ToString(System.Globalization.CultureInfo.InvariantCulture)))];

        UpdateDownloadPlan plan = UpdateService.PlanDownload(Info(Full(90_000_000), Full(80_000_000), eleven));

        Assert.Equal(90_000_000, plan.TotalBytes);
    }

    /// <summary>Exactly ten is still eligible — the rule is "more than ten", not "ten or more".</summary>
    [Fact]
    public void WithExactlyTenDeltas_ThePlanStillCountsThem()
    {
        VelopackAsset[] ten = [.. Enumerable.Range(1, 10).Select(i => Delta(100_000, i.ToString(System.Globalization.CultureInfo.InvariantCulture)))];

        UpdateDownloadPlan plan = UpdateService.PlanDownload(Info(Full(90_000_000), Full(80_000_000), ten));

        Assert.Equal(1_000_000, plan.TotalBytes);
    }

    /// <summary>
    /// Deltas that are not actually smaller are discarded. Velopack compares their sum against the
    /// full package, so a long chain of large deltas downloads the full one instead.
    /// </summary>
    [Fact]
    public void WhenTheDeltasAreNotSmaller_ThePlanIsTheFullPackage()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(
            Info(Full(5_000_000), Full(4_000_000), Delta(3_000_000, "1"), Delta(3_000_000, "2")));

        Assert.Equal(5_000_000, plan.TotalBytes);
    }

    /// <summary>Equal is still eligible — Velopack's test is <c>&lt;=</c>.</summary>
    [Fact]
    public void WhenTheDeltasSumExactlyToTheFullSize_TheyAreStillUsed()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(
            Info(Full(6_000_000), Full(4_000_000), Delta(3_000_000, "1"), Delta(3_000_000, "2")));

        Assert.Equal(6_000_000, plan.TotalBytes);
    }

    [Fact]
    public void WithNoDeltas_ThePlanIsTheFullPackage()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(Info(Full(90_000_000), Full(80_000_000)));

        Assert.Equal(90_000_000, plan.TotalBytes);
    }

    /// <summary>
    /// A release advertising no size. Zero means "unknown" downstream, which hides the byte readout
    /// rather than showing a download of nothing.
    /// </summary>
    [Fact]
    public void WithNothingToGoOn_ThePlanIsUnknown()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(Info(Full(0), null));

        Assert.False(plan.IsKnown);
        Assert.Equal(0, plan.TotalBytes);
    }

    /// <summary>
    /// The join: <c>CheckAsync</c> has to actually attach the plan to the info it hands back. Every
    /// other test here calls <see cref="UpdateService.PlanDownload"/> directly, so dropping the
    /// argument at the call site leaves them all green while the window loses its byte readout.
    /// <para>
    /// <c>CheckAsync</c> itself cannot be driven without an installed Velopack layout, so this
    /// asserts the wiring at the type that carries it: an <c>UpdateAvailableInfo</c> built without a
    /// plan must be UNKNOWN rather than quietly defaulting to something plausible.
    /// </para>
    /// </summary>
    [Fact]
    public void AnInfoBuiltWithoutAPlan_CarriesUnknownRatherThanAGuess()
    {
        UpdateAvailableInfo withoutPlan = new("9.9.9", new object());
        UpdateAvailableInfo withPlan = new("9.9.9", new object(), UpdateDownloadPlan.Full(1234));

        Assert.False(withoutPlan.DownloadPlan.IsKnown);
        Assert.Equal(1234, withPlan.DownloadPlan.TotalBytes);
    }

    /// <summary>Sizes that cannot be summed without overflowing are not sizes.</summary>
    [Fact]
    public void WithSizesThatWouldOverflow_ThePlanFallsBackRatherThanWrapping()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(
            Info(Full(90_000_000), Full(80_000_000), Delta(long.MaxValue, "1"), Delta(long.MaxValue, "2")));

        Assert.Equal(90_000_000, plan.TotalBytes);
    }
}
