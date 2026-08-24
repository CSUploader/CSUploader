// <copyright file="UpdateServiceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.Lib.Update;
using Moq;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace CSUploader.Tests.Lib.Update;

/// <summary>
/// Covers the branch of <see cref="UpdateService.CheckAsync"/> a test process actually reaches: a
/// test run is never Velopack-installed, so <c>IsInstalled</c> is false and the check goes to the
/// release source directly.
/// <para>
/// Every test here drives that through the injected <see cref="IUpdateSource"/>. That is not a
/// stylistic choice — before the seam existed this file called the real service, which was harmless
/// only while the no-install branch returned instantly without touching the network. Now that it
/// reads the feed for real, the same test would have put a live GitHub request in the unit suite,
/// failing on any offline machine and rate-limiting CI.
/// </para>
/// <para>
/// The installed branch wraps the concrete (non-mockable) Velopack <c>UpdateManager</c> and cannot be
/// driven from here at all; it is exercised through the <c>MainViewModel</c> contract with a mocked
/// <c>IUpdateService</c> (MainViewModelUpdateTests).
/// </para>
/// </summary>
public class UpdateServiceTests
{
    // Velopack's locator is a process-global static that UpdateManager construction queries; initialise it
    // once so `new UpdateService(...)` doesn't throw "No VelopackLocator has been set" (idempotent no-op if
    // another test class already ran it).
    private static readonly object VelopackInit = InitVelopack();

    private static object InitVelopack()
    {
        VelopackApp.Build().Run();
        return new object();
    }

    private static VelopackAsset Asset(string version, VelopackAssetType type = VelopackAssetType.Full)
        => new()
        {
            PackageId = "CSUploader",
            Version = SemanticVersion.Parse(version),
            Type = type,
            FileName = $"CSUploader-{version}-full.nupkg",
        };

    private static UpdateService Service(IUpdateSource source, string currentVersion = "1.5.0")
    {
        _ = VelopackInit;
        return new UpdateService(Mock.Of<IAppLogger>(), source, currentVersion);
    }

    [Fact]
    public async Task WhenTheFeedHasANewerRelease_ItIsReportedAsNotInstallable()
    {
        RecordingSource source = new(new VelopackAssetFeed { Assets = [Asset("1.5.0"), Asset("1.6.0")] });

        UpdateCheckResult result = await Service(source).CheckAsync();

        Assert.Equal(UpdateCheckStatus.AvailableNotInstallable, result.Status);
        Assert.Equal("1.6.0", result.NewVersion);

        // The half that matters more than the status: no payload. UpdateAvailableInfo.Payload
        // promises a Velopack UpdateInfo the install path can act on, and there is none.
        Assert.Null(result.Info);
    }

    [Fact]
    public async Task WhenTheFeedHasNothingNewer_ItIsUpToDate()
    {
        RecordingSource source = new(new VelopackAssetFeed { Assets = [Asset("1.4.0"), Asset("1.5.0")] });

        UpdateCheckResult result = await Service(source).CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task WhenTheFeedIsEmpty_ItIsUpToDate()
    {
        RecordingSource source = new(new VelopackAssetFeed { Assets = [] });

        UpdateCheckResult result = await Service(source).CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    /// <summary>
    /// Deltas are not releases. A delta stamped 1.6.0 exists only to be applied on top of a local
    /// package, so picking one as "the newest release" would announce a version that cannot be
    /// downloaded whole — and on a build that cannot install anything, never resolve.
    /// </summary>
    [Fact]
    public async Task ADeltaIsNotMistakenForARelease()
    {
        RecordingSource source = new(new VelopackAssetFeed
        {
            Assets = [Asset("1.5.0"), Asset("1.6.0", VelopackAssetType.Delta)],
        });

        UpdateCheckResult result = await Service(source).CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    /// <summary>
    /// The request has to be the one <c>UpdateManager</c> would have made. The channel decides which
    /// <c>releases.{channel}.json</c> is read, so getting it wrong reads someone else's feed — or a
    /// 404 that surfaces as "update check failed" for ever.
    /// </summary>
    [Fact]
    public async Task TheFeedIsAskedForThisPlatformsChannel()
    {
        RecordingSource source = new(new VelopackAssetFeed { Assets = [] });

        await Service(source).CheckAsync();

        Assert.Equal(VelopackRuntimeInfo.SystemOs.GetOsShortName(), source.Channel);
    }

    [Fact]
    public async Task WhenTheFeedThrows_TheCheckFailsRatherThanPropagating()
    {
        ThrowingSource source = new(new HttpRequestException("no network"));

        UpdateCheckResult result = await Service(source).CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("no network", result.FailureReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// "We could not read our own version" must not become "there is an update". Every published
    /// release sorts above the unknown-version sentinel, so treating it as a real version would
    /// announce an update on every check, for ever, on any host where the version cannot be read.
    /// </summary>
    [Theory]
    [InlineData(AppVersion.Unknown)]
    [InlineData("not-a-version")]
    [InlineData("")]
    public void AnUnreadableCurrentVersionFailsTheCheckInsteadOfFindingAnUpdate(string currentVersion)
    {
        UpdateCheckResult result = UpdateService.DescribeWithoutInstall([Asset("1.6.0")], currentVersion);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    private sealed class RecordingSource(VelopackAssetFeed feed) : IUpdateSource
    {
        public string? Channel { get; private set; }

        public Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            Channel = channel;
            return Task.FromResult(feed);
        }

        public Task DownloadReleaseEntry(
            IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
            => throw new NotSupportedException("A check must never download.");
    }

    private sealed class ThrowingSource(Exception fault) : IUpdateSource
    {
        public Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
            => throw fault;

        public Task DownloadReleaseEntry(
            IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
            => throw new NotSupportedException("A check must never download.");
    }
}
