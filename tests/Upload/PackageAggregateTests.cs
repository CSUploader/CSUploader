// <copyright file="PackageAggregateTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using Moq;
using Xunit;

namespace CSUploader.Tests.Upload;

/// <summary>
/// Covers <see cref="Package.ComputeAggregate"/> — the single-pass rollup that replaced the ~9 separate
/// aggregate getters the Uploads footer used to sum per field. Each field must match the corresponding
/// old "<c>p.X ?? 0</c>" sum exactly (that's what keeps the footer's displayed totals unchanged), so these
/// pin the byte sums, the terminal-state counts, and the active-only speed gate.
/// </summary>
public class PackageAggregateTests
{
    [Fact]
    public void ComputeAggregate_SumsBytesAndCounts_GatingSpeedToActiveFiles()
    {
        Package pkg = MakePackage();
        pkg.AddPackageFiles(
        [
            MakeFile(pkg, size: 1000, loaded: 400, remaining: 600, speed: 50, FileState.Uploading),
            MakeFile(pkg, size: 2000, loaded: 2000, remaining: null, speed: null, FileState.Completed),
            MakeFile(pkg, size: 500, loaded: null, remaining: 500, speed: null, FileState.Failed),
            MakeFile(pkg, size: 300, loaded: 0, remaining: 300, speed: null, FileState.Cancelled),
        ]);

        PackageAggregate agg = pkg.ComputeAggregate();

        Assert.Equal(4, agg.FileCount);
        Assert.Equal(3800, agg.TotalBytes);        // 1000+2000+500+300
        Assert.Equal(2400, agg.BytesLoaded);       // 400+2000+0 (Failed's null → 0)
        Assert.Equal(1400, agg.BytesRemaining);    // 600+500+300 (Completed's null → 0)
        Assert.Equal(50, agg.Speed);               // one Uploading file with a speed → gate open
        Assert.Equal(1, agg.Uploading);
        Assert.Equal(1, agg.Completed);
        Assert.Equal(1, agg.Failed);
        Assert.Equal(1, agg.Cancelled);
    }

    [Fact]
    public void ComputeAggregate_Speed_IsZero_WhenNoFileIsActivelyTransferring()
    {
        // A paused/stopped file can still carry a stale Speed value; the footer's "p.Speed ?? 0" reports 0
        // unless a file is actively hashing/uploading, and ComputeAggregate must honour the same gate.
        Package pkg = MakePackage();
        pkg.AddPackageFiles([MakeFile(pkg, size: 1000, loaded: 100, remaining: 900, speed: 999, FileState.Paused)]);

        Assert.Equal(0, pkg.ComputeAggregate().Speed);
    }

    [Fact]
    public void ComputeAggregate_EmptyPackage_IsAllZero()
        => Assert.Equal(default, MakePackage().ComputeAggregate());

    private static Package MakePackage()
    {
        FileHosterClient hoster = new("TestHost", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "TestHost", IsAnonymous = true };
        return new Package(new PackageOptions
        {
            Title = "p",
            Logger = Mock.Of<IAppLogger>(),
            Settings = new AppSettings(),
            FileHosters = new() { { hoster, login } },
        });
    }

    private static PackageFile MakeFile(Package pkg, long? size, long? loaded, long? remaining, long? speed, FileState state)
    {
        FileHosterClient hoster = pkg.FileHosterLogins.Keys.First();
        FileHosterLoginDto login = pkg.FileHosterLogins.Values.First();
        return new PackageFile(pkg, @"C:\d\f.bin", hoster, login)
        {
            Size = size,
            BytesLoaded = loaded,
            BytesRemaining = remaining,
            Speed = speed,
            State = state,
        };
    }
}
