// <copyright file="PackageDurationTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using Moq;

namespace CSUploader.Tests.Upload;

/// <summary>
/// A package's Elapsed is a wall-clock SPAN, not the sum of its files' durations. It used to sum,
/// which reads as roughly (concurrency × real time) — a real run of 81 parallel files reported
/// 4h59m48s for a 17-minute upload, which is what these pin against.
/// </summary>
public class PackageDurationTests
{
    private static readonly DateTime T0 = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Duration_ParallelFiles_IsTheSpan_NotTheSumOfTheirDurations()
    {
        // Three files running concurrently, ~4 minutes each, overlapping inside a 5-minute window.
        Package pkg = MakePackage();
        pkg.AddPackageFiles([
            MakeFile(pkg, T0, T0.AddMinutes(4)),
            MakeFile(pkg, T0.AddMinutes(0.5), T0.AddMinutes(4.5)),
            MakeFile(pkg, T0.AddMinutes(1), T0.AddMinutes(5)),
        ]);

        // Span = first start → last finish. The old sum would have been ~12 minutes.
        Assert.Equal(TimeSpan.FromMinutes(5), pkg.Duration);
    }

    [Fact]
    public void Duration_ScalesWithTheRun_NotWithTheFileCount()
    {
        // The reported symptom, in miniature: many files inside one short window must not multiply.
        Package pkg = MakePackage();
        List<PackageFile> files = [];
        for (int i = 0; i < 81; i++)
        {
            files.Add(MakeFile(pkg, T0, T0.AddMinutes(4)));
        }

        pkg.AddPackageFiles([.. files]);

        Assert.Equal(TimeSpan.FromMinutes(4), pkg.Duration); // not 81 × 4 minutes
    }

    [Fact]
    public void Duration_SequentialFilesWithAGap_IncludesTheGap()
    {
        // A span is a span: idle time between files counts, which is what makes this agree with the
        // Overview's elapsed clock.
        Package pkg = MakePackage();
        pkg.AddPackageFiles([
            MakeFile(pkg, T0, T0.AddMinutes(1)),
            MakeFile(pkg, T0.AddMinutes(10), T0.AddMinutes(11)),
        ]);

        Assert.Equal(TimeSpan.FromMinutes(11), pkg.Duration); // not the 2 minutes of transfer
    }

    [Fact]
    public void Duration_WhileAFileIsStillInFlight_RunsToNow()
    {
        Package pkg = MakePackage();
        pkg.AddPackageFiles([
            MakeFile(pkg, DateTime.Now.AddMinutes(-10), DateTime.Now.AddMinutes(-9)),
            MakeFile(pkg, DateTime.Now.AddMinutes(-5), finished: null), // still uploading
        ]);

        TimeSpan? elapsed = pkg.Duration;
        Assert.NotNull(elapsed);

        // ~10 minutes and counting — measured from the EARLIEST start, not the in-flight file's.
        Assert.InRange(elapsed!.Value, TimeSpan.FromMinutes(9.5), TimeSpan.FromMinutes(10.5));
    }

    [Fact]
    public void Duration_NoFileHasStarted_IsNull()
    {
        Package pkg = MakePackage();
        pkg.AddPackageFiles([MakeFile(pkg, started: null, finished: null)]);

        Assert.Null(pkg.Duration); // renders as an empty cell, not "00s"
    }

    [Fact]
    public void Duration_EmptyPackage_IsNull()
        => Assert.Null(MakePackage().Duration);

    [Fact]
    public void Duration_FinishBeforeStart_ClampsToZero()
    {
        // A clock step (NTP correction, DST) must never render a negative elapsed.
        Package pkg = MakePackage();
        pkg.AddPackageFiles([MakeFile(pkg, T0, T0.AddMinutes(-3))]);

        Assert.Equal(TimeSpan.Zero, pkg.Duration);
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

    private static PackageFile MakeFile(Package pkg, DateTime? started, DateTime? finished)
    {
        FileHosterClient hoster = pkg.FileHosterLogins.Keys.First();
        FileHosterLoginDto login = pkg.FileHosterLogins.Values.First();
        return new PackageFile(pkg, @"C:\d\f.bin", hoster, login)
        {
            StartedDate = started,
            FinishedDate = finished,

            // Deliberately set: the package must IGNORE the per-file duration and span the dates
            // instead. If it ever went back to summing, these would make it obvious.
            Duration = started is { } s && finished is { } f ? f - s : null,
        };
    }
}
