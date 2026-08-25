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
/// percentage is a byte fraction on one path and not on the other.
/// </para>
/// <para>
/// The two ways of being wrong are not symmetrical. Answering "delta" for what is really a full
/// download only hides a readout. Answering "full" for what is really deltas feeds the full
/// package's size into an aggregate delta percentage and puts a WRONG number on screen — so the
/// conditions below are the conservative ones. The rules were read from
/// <c>UpdateManager.DownloadUpdatesAsync</c> in Velopack 1.2.0, and nothing here can detect a future
/// version loosening one of them; that limit is real and unguarded.
/// </para>
/// </summary>
public class UpdateServicePlanTests
{
    private static VelopackAsset Asset(long size, VelopackAssetType type, string version) => new()
    {
        FileName = $"CSUploader-{version}-{type}.nupkg",
        Version = SemanticVersion.Parse($"1.0.{version}"),
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
    /// claimed. Velopack's own <c>Sum</c> throws on an overflow, and OUTSIDE its delta-fallback
    /// handler, so nothing gets fetched at all — there is no download for a size to describe.
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

    /// <summary>
    /// The NEGATIVE half of the overflow guard, which the pair of -1s above cannot reach because
    /// they add up perfectly well. <see cref="long.MinValue"/> followed by -1 underflows and wraps
    /// to <see cref="long.MaxValue"/>, which then looks too large for the delta path — so an
    /// unguarded sum answers with the full package's size for a download Velopack will not perform.
    /// </summary>
    [Fact]
    public void WhenTheDeltaSizesUnderflow_NoSizeIsClaimed()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(
            Info(Full(90_000_000), Full(80_000_000), Delta(long.MinValue, "1"), Delta(-1, "2")));

        Assert.False(plan.IsKnown);
    }

    /// <summary>
    /// The case that actually pins the overflow guard rather than passing by luck.
    /// <para>
    /// Two <see cref="long.MaxValue"/> deltas wrap to -2, which still looks "small enough" and lands
    /// on the delta path — so an unguarded sum reaches the same answer for the wrong reason. A THIRD
    /// wraps back to a huge positive, which looks "too large", and an unguarded sum would then
    /// announce a 90 MB full download that Velopack will never perform: its own Sum throws on this
    /// metadata, outside the delta-fallback handler, so nothing is fetched at all.
    /// </para>
    /// </summary>
    [Fact]
    public void WhenTheDeltaSizesWrapBackAroundToLookPlausible_NoSizeIsClaimed()
    {
        UpdateDownloadPlan plan = UpdateService.PlanDownload(Info(
            Full(90_000_000),
            Full(80_000_000),
            Delta(long.MaxValue, "1"),
            Delta(long.MaxValue, "2"),
            Delta(long.MaxValue, "3")));

        Assert.False(plan.IsKnown);
    }

    /// <summary>
    /// The join. Everything else here calls <see cref="UpdateService.PlanDownload"/> directly, so
    /// the plan reaching the info the app actually receives is a separate fact — and one that
    /// nothing could reach until <c>Describe</c> was split out of <c>CheckAsync</c>, which needs an
    /// installed Velopack layout to run at all.
    /// </summary>
    [Fact]
    public void Describe_AttachesThePlanToTheInfoTheAppReceives()
    {
        UpdateCheckResult result = UpdateService.Describe(Info(Full(90_000_000), Full(80_000_000)));

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.NotNull(result.Info);
        Assert.Equal(90_000_000, result.Info!.DownloadPlan.TotalBytes);
    }

    /// <summary>An eligible delta set reaches the app as no size, not as a missing update.</summary>
    [Fact]
    public void Describe_CarriesTheDeltaPathsAbsenceOfASize()
    {
        UpdateCheckResult result = UpdateService.Describe(
            Info(Full(90_000_000), Full(80_000_000), Delta(1_000_000, "1")));

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.False(result.Info!.DownloadPlan.IsKnown);
    }

    [Fact]
    public void Describe_WithNothingFound_IsUpToDate()
        => Assert.Equal(UpdateCheckStatus.UpToDate, UpdateService.Describe(null).Status);

    /// <summary>
    /// The other attachment Describe makes: the release notes vpk embedded in the package reach
    /// the info the prompt receives, and a package with none (everything packed before CI passed
    /// --releaseNotes) reaches it as null rather than as an empty what's-new section.
    /// </summary>
    [Fact]
    public void Describe_CarriesTheEmbeddedReleaseNotes()
    {
        UpdateInfo info = Info(Full(90_000_000), Full(80_000_000));
        info.TargetFullRelease.NotesMarkdown = "## What's new\n- something";

        UpdateCheckResult result = UpdateService.Describe(info);

        Assert.Equal("## What's new\n- something", result.Info!.ReleaseNotesMarkdown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Describe_MapsBlankNotesToNull(string? notes)
    {
        UpdateInfo info = Info(Full(90_000_000), Full(80_000_000));
        info.TargetFullRelease.NotesMarkdown = notes!;

        Assert.Null(UpdateService.Describe(info).Info!.ReleaseNotesMarkdown);
    }
}
