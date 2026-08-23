// <copyright file="SpeedLimiterScopeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using CSUploader.Tests.TestSupport;
using CSUploader.Upload;
using Moq;

namespace CSUploader.Tests.Upload;

/// <summary>
/// Which bucket each scope resolves to. These assert IDENTITY, not rate: the buckets here are built
/// without an injected clock, so draining them would run on the system clock — tokens accruing
/// between iterations, assertions overshooting, and at a high limit a drain loop that may never
/// terminate. Rate behaviour is pinned deterministically in <c>SpeedLimiterTests</c>; what belongs
/// here is who shares with whom, and <c>ConditionalWeakTable</c> makes reference identity the exact
/// expression of that.
/// </summary>
public class SpeedLimiterScopeTests
{
    [Fact]
    public void TwoFilesInOnePackage_ResolveToOneBucket()
    {
        Package package = SpeedLimitTestFactory.Package(new AppSettings(), packageLimitKBps: 100);

        Assert.Same(package.First().SpeedLimiter, package.Skip(1).First().SpeedLimiter);
    }

    [Fact]
    public void AFileWithItsOwnLimit_ResolvesToItsOwnBucket()
    {
        Package package = SpeedLimitTestFactory.Package(new AppSettings(), packageLimitKBps: 100);
        PackageFile capped = package.First();
        capped.SpeedLimitKBps = 200;

        Assert.NotSame(capped.SpeedLimiter, package.Skip(1).First().SpeedLimiter);
        Assert.Equal(200L * 1024, capped.SpeedLimiter.CurrentLimitBytesPerSecond);
    }

    /// <summary>Ownership is a live question, not a construction-time one — the user can clear an
    /// override while the file is uploading.</summary>
    [Fact]
    public void ClearingAFileOverride_ResolvesBackToThePackagesBucket()
    {
        Package package = SpeedLimitTestFactory.Package(new AppSettings(), packageLimitKBps: 100);
        PackageFile file = package.First();
        file.SpeedLimitKBps = 200;
        Assert.NotSame(package.SpeedLimiter, file.SpeedLimiter);

        file.SpeedLimitKBps = null;

        Assert.Same(package.SpeedLimiter, file.SpeedLimiter);
    }

    [Fact]
    public void PackagesWithNoOverride_ResolveToTheOneGlobalBucket()
    {
        AppSettings settings = new() { SpeedLimit = 100 };
        Package a = SpeedLimitTestFactory.Package(settings, packageLimitKBps: null);
        Package b = SpeedLimitTestFactory.Package(settings, packageLimitKBps: null);

        Assert.Same(a.SpeedLimiter, b.SpeedLimiter);
    }

    /// <summary>
    /// The settled product decision, pinned so nobody "fixes" it later: the cascade is an OVERRIDE,
    /// so a package limit replaces the global one rather than nesting inside it — on a separate
    /// bucket, which is what lets the machine-wide aggregate reach 600 KiB/s here.
    /// </summary>
    [Fact]
    public void AnOverrideMayExceedTheGlobalLimit_AndGetsItsOwnBucketToDoIt()
    {
        AppSettings settings = new() { SpeedLimit = 100 };
        Package overriding = SpeedLimitTestFactory.Package(settings, packageLimitKBps: 500);
        Package inheriting = SpeedLimitTestFactory.Package(settings, packageLimitKBps: null);

        Assert.NotSame(overriding.SpeedLimiter, inheriting.SpeedLimiter);
        Assert.Equal(500L * 1024, overriding.SpeedLimiter.CurrentLimitBytesPerSecond);
        Assert.Equal(100L * 1024, inheriting.SpeedLimiter.CurrentLimitBytesPerSecond);
    }

    /// <summary>
    /// The ordinary case must not enter the side table at all: a CWT lookup locks and allocates on
    /// first access, which would make "unlimited costs nothing" false for almost every user.
    /// </summary>
    [Fact]
    public void AnUnlimitedGlobal_ResolvesToTheSharedUnlimitedInstance()
    {
        Package package = SpeedLimitTestFactory.Package(new AppSettings(), packageLimitKBps: null);

        Assert.Same(SpeedLimiter.Unlimited, package.SpeedLimiter);
        Assert.Same(SpeedLimiter.Unlimited, package.First().SpeedLimiter);
    }

    /// <summary>
    /// The race a lazy <c>??=</c> loses: the scheduler builds attempt inputs from concurrent
    /// <c>Task.Run</c> workers, so two files in one package could each construct their own bucket.
    /// </summary>
    [Fact]
    public async Task ConcurrentFirstAccess_YieldsOneBucket()
    {
        Package package = SpeedLimitTestFactory.Package(new AppSettings(), packageLimitKBps: 100, fileCount: 32);
        using Barrier barrier = new(32);

        SpeedLimiter[] buckets = await Task.WhenAll(package.Select(file => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return file.SpeedLimiter;
        })));

        Assert.Single(buckets.Distinct());
    }

    /// <summary>
    /// The plumbing join: what BuildAttemptInputs carries must RESOLVE to the file's bucket, not
    /// merely be non-null — it could otherwise carry SpeedBudget.Unlimited and look fine.
    /// </summary>
    [Fact]
    public void BuildAttemptInputs_CarriesABudgetResolvingToThatFilesBucket()
    {
        Package package = SpeedLimitTestFactory.Package(new AppSettings(), packageLimitKBps: 100);
        PackageFile a = package.First();
        PackageFile b = package.Skip(1).First();

        SpeedBudget budgetA = a.BuildAttemptInputs(Mock.Of<IAppLogger>()).SpeedBudget;
        SpeedBudget budgetB = b.BuildAttemptInputs(Mock.Of<IAppLogger>()).SpeedBudget;

        Assert.Same(package.SpeedLimiter, budgetA.CurrentLimiter);
        Assert.Same(budgetA.CurrentLimiter, budgetB.CurrentLimiter);
    }
}
