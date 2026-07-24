// <copyright file="AppDataPathsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.Lib;

namespace CSUploader.Tests.Lib;

/// <summary>
/// Pins the SQLite database path resolution across platforms. The Windows branch MUST stay beside
/// the executable (the shipped v1.0.0 location — moving it would orphan existing users' data); the
/// non-Windows branch MUST live under the per-user data dir, because a packaged AppImage runs from a
/// read-only mount where SQLite can't create the file beside the exe (Error 14).
/// </summary>
public sealed class AppDataPathsTests
{
    [Fact]
    public void ComposeDbPath_Windows_SitsBesideBaseDirectory()
    {
        string baseDir = Path.Combine("C", "app", "current");

        string path = AppDataPaths.ComposeDbPath(isWindows: true, baseDirectory: baseDir, localAppData: Path.Combine("C", "Users", "x", "AppData", "Local"));

        Assert.Equal(Path.Combine(baseDir, "CSUploader.db"), path);
    }

    [Fact]
    public void ComposeDbPath_NonWindows_SitsUnderPerUserDataDir()
    {
        string localAppData = Path.Combine("home", "x", ".local", "share");

        string path = AppDataPaths.ComposeDbPath(isWindows: false, baseDirectory: Path.Combine("tmp", "mount", "usr", "bin"), localAppData: localAppData);

        // Under <localAppData>/CSUploader/CSUploader.db — NOT beside the (read-only) base directory.
        Assert.Equal(Path.Combine(localAppData, "CSUploader", "CSUploader.db"), path);
    }

    [Fact]
    public void ComposeDbPath_NonWindows_IgnoresBaseDirectory()
    {
        string a = AppDataPaths.ComposeDbPath(isWindows: false, baseDirectory: "/read/only/mount", localAppData: "/data");
        string b = AppDataPaths.ComposeDbPath(isWindows: false, baseDirectory: "/somewhere/else", localAppData: "/data");

        Assert.Equal(a, b); // the non-Windows path never depends on where the app happens to run from
    }
}
