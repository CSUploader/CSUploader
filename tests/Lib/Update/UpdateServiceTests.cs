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
}
