// <copyright file="UpdateServiceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.Lib.Update;
using Moq;
using Velopack;

namespace CSUploader.Tests.Lib.Update;

/// <summary>
/// Unit coverage for the one <see cref="UpdateService.CheckAsync"/> branch reachable without a
/// Velopack-installed layout: a test run is never installed, so <c>IsInstalled</c> is false and the
/// check short-circuits to <see cref="UpdateCheckStatus.NotInstalled"/>. The UpToDate/Available/Failed
/// branches wrap the concrete (non-mockable) Velopack <c>UpdateManager</c> and are exercised through the
/// <c>MainViewModel</c> contract with a mocked <c>IUpdateService</c> (MainViewModelUpdateTests).
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

    [Fact]
    public async Task CheckAsync_WhenNotInstalled_ReturnsNotInstalled()
    {
        _ = VelopackInit;
        UpdateService svc = new(Mock.Of<IAppLogger>());

        UpdateCheckResult result = await svc.CheckAsync();

        Assert.Equal(UpdateCheckStatus.NotInstalled, result.Status);
    }

    private static VelopackAsset Asset(long size, VelopackAssetType type) => new()
    {
        FileName = $"CSUploader-{size}-{type}.nupkg",
        Size = size,
        Type = type,
    };

    /// <summary>
    /// What the byte readout in the update window counts against. Velopack applies deltas when it
    /// has them, so the delta total is what will actually move — using the full package's size
    /// instead would report a download several times larger than the one happening, and the rate
    /// with it.
    /// </summary>
    [Fact]
    public void EstimateDownloadBytes_PrefersTheDeltasVelopackWillActuallyFetch()
    {
        UpdateInfo info = new(
            Asset(90_000_000, VelopackAssetType.Full),
            isDowngrade: false,
            null,
            [Asset(3_000_000, VelopackAssetType.Delta), Asset(1_500_000, VelopackAssetType.Delta)]);

        Assert.Equal(4_500_000, UpdateService.EstimateDownloadBytes(info));
    }

    /// <summary>With no deltas there is only the full package, which is then exactly right.</summary>
    [Fact]
    public void EstimateDownloadBytes_FallsBackToTheFullPackage()
    {
        UpdateInfo info = new(Asset(90_000_000, VelopackAssetType.Full), isDowngrade: false, null, []);

        Assert.Equal(90_000_000, UpdateService.EstimateDownloadBytes(info));
    }

    /// <summary>
    /// A release that advertises no size at all. Zero means "unknown" downstream, which hides the
    /// byte readout rather than showing a download of nothing.
    /// </summary>
    [Fact]
    public void EstimateDownloadBytes_WithNothingToGoOn_IsZero()
    {
        UpdateInfo info = new(Asset(0, VelopackAssetType.Full), isDowngrade: false, null, []);

        Assert.Equal(0, UpdateService.EstimateDownloadBytes(info));
    }
}
