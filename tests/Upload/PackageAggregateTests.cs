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

    [Fact]
    public void NotifyChangedRows_SteadyState_RefreshesOnlyRunningFilesAndTheirPackage()
    {
        Package pkg = MakePackage();
        PackageFile running = MakeFile(pkg, size: 1000, loaded: 100, remaining: 900, speed: 50, FileState.Uploading);
        PackageFile idle = MakeFile(pkg, size: 1000, loaded: null, remaining: 1000, speed: null, FileState.UploadQueued);
        pkg.AddPackageFiles([running, idle]);
        pkg.NotifyChangedRows(); // first pass acknowledges both current states

        int pkgRaises = 0, runningRaises = 0, idleRaises = 0;
        pkg.PropertyChanged += (_, _) => pkgRaises++;
        running.PropertyChanged += (_, _) => runningRaises++;
        idle.PropertyChanged += (_, _) => idleRaises++;

        pkg.NotifyChangedRows(); // steady state: only the still-running file changes

        Assert.True(runningRaises > 0);  // running file refreshed (progress ticks)
        Assert.True(pkgRaises > 0);      // package aggregate refreshed (a child is running)
        Assert.Equal(0, idleRaises);     // the unchanged queued row is skipped — no notify storm
    }

    [Fact]
    public void NotifyChangedRows_NotifiesOnStateTransition_ThenSkipsWhenStable()
    {
        Package pkg = MakePackage();
        PackageFile file = MakeFile(pkg, size: 1000, loaded: 1000, remaining: null, speed: null, FileState.UploadQueued);
        pkg.AddPackageFiles([file]);
        pkg.NotifyChangedRows(); // acknowledge Idle→UploadQueued

        // State has a plain setter (no PropertyChanged of its own) — the transition must still surface.
        file.State = FileState.Completed;

        int raises = 0;
        file.PropertyChanged += (_, _) => raises++;
        pkg.NotifyChangedRows();
        Assert.True(raises > 0); // transition surfaced exactly on the tick that saw it

        raises = 0;
        pkg.NotifyChangedRows();
        Assert.Equal(0, raises); // a stable Completed row is not re-notified
    }

    [Fact]
    public void NotifyChangedRows_NotifiesEachRunningFileOnce_NoPackageCascadeDoubleUp()
    {
        // The old tick notified an expanded file twice — once via the package cascade, once as its own row.
        // NotifyChangedRows notifies the package's own props (no cascade) and the file once.
        Package pkg = MakePackage();
        PackageFile running = MakeFile(pkg, size: 1000, loaded: 100, remaining: 900, speed: 50, FileState.Uploading);
        pkg.AddPackageFiles([running]);
        pkg.NotifyChangedRows(); // acknowledge

        int progressRaises = 0;
        running.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PackageFile.Progress))
            {
                progressRaises++;
            }
        };
        pkg.NotifyChangedRows();

        Assert.Equal(1, progressRaises);
    }

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
