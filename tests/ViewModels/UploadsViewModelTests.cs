// <copyright file="UploadsViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using CSUploader.ViewModels;
using Moq;

namespace CSUploader.Tests.ViewModels;

public class UploadsViewModelTests
{
    // ---- Multi-select context-menu helpers (operate on the whole grid selection) ----

    [Fact]
    public void SelectedDistinctUrls_ReturnsDistinctNonEmptyUrls_SkippingPackagesAndBlanks()
    {
        (Package pkg, FileHosterClient h, FileHosterLoginDto l) = MakePackage();
        PackageFile a = MakeFile(pkg, h, l, @"C:\d\a.bin", "https://x/a");
        PackageFile b = MakeFile(pkg, h, l, @"C:\d\b.bin", "https://x/b");
        PackageFile dupUrl = MakeFile(pkg, h, l, @"C:\d\c.bin", "https://x/a"); // same URL as a
        PackageFile noUrl = MakeFile(pkg, h, l, @"C:\d\d.bin", null);

        IReadOnlyList<string> urls = UploadsViewModel.SelectedDistinctUrls(
            new List<object> { pkg, a, b, dupUrl, noUrl });

        // Distinct, selection order preserved; the Package row and the URL-less file contribute nothing.
        Assert.Equal(new[] { "https://x/a", "https://x/b" }, urls);
    }

    [Fact]
    public void CanOpenUrl_TrueOnlyWhenSomeSelectedFileHasAUrl()
    {
        (Package pkg, FileHosterClient h, FileHosterLoginDto l) = MakePackage();
        PackageFile withUrl = MakeFile(pkg, h, l, @"C:\d\a.bin", "https://x/a");
        PackageFile noUrl = MakeFile(pkg, h, l, @"C:\d\b.bin", null);

        Assert.True(UploadsViewModel.CanOpenUrl(new List<object> { noUrl, withUrl }));
        Assert.False(UploadsViewModel.CanOpenUrl(new List<object> { pkg, noUrl }));
        Assert.False(UploadsViewModel.CanOpenUrl(null));
    }

    [Fact]
    public void SelectedDistinctDirectories_DedupesByDirectory_KeepingExistingOnes()
    {
        (Package pkg, FileHosterClient h, FileHosterLoginDto l) = MakePackage();
        PackageFile a = MakeFile(pkg, h, l, @"C:\src\pd\a.bin", null);  // dir C:\src\pd
        PackageFile b = MakeFile(pkg, h, l, @"C:\src\pd\b.bin", null);  // same dir
        PackageFile c = MakeFile(pkg, h, l, @"C:\other\c.bin", null);   // different dir
        PackageFile gone = MakeFile(pkg, h, l, @"C:\missing\x.bin", null);

        IReadOnlyList<string> dirs = UploadsViewModel.SelectedDistinctDirectories(
            new List<object> { a, b, c, gone },
            dir => dir != @"C:\missing"); // pretend C:\missing no longer exists

        // One entry per distinct existing directory, in selection order; the same-folder dup folds.
        Assert.Equal(new[] { @"C:\src\pd", @"C:\other" }, dirs);
    }

    [Fact]
    public void DistinctCompletedCount_CountsPackageAndItsSelectedChildOnce()
    {
        (Package pkg, FileHosterClient h, FileHosterLoginDto l) = MakePackage();
        PackageFile a = MakeFile(pkg, h, l, @"C:\d\a.bin", null);
        PackageFile b = MakeFile(pkg, h, l, @"C:\d\b.bin", null);
        pkg.AddPackageFiles(new[] { a, b });
        a.State = FileState.Completed; // one completed file in the package

        // Selecting the package AND its already-counted completed child must not double-count it.
        Assert.Equal(1, UploadsViewModel.DistinctCompletedCount(new object[] { pkg, a }));
        // A loose completed file with no selected parent package counts on its own.
        Assert.Equal(1, UploadsViewModel.DistinctCompletedCount(new object[] { a }));
    }

    private static (Package Package, FileHosterClient Hoster, FileHosterLoginDto Login) MakePackage()
    {
        FileHosterClient hoster = new("TestHost", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "TestHost", IsAnonymous = true };
        PackageOptions options = new()
        {
            Title = "p",
            Logger = Mock.Of<IAppLogger>(),
            Settings = new AppSettings(),
            FileHosters = new() { { hoster, login } },
        };
        return (new Package(options), hoster, login);
    }

    private static PackageFile MakeFile(Package pkg, FileHosterClient hoster, FileHosterLoginDto login, string path, string? url)
    {
        PackageFile file = new(pkg, path, hoster, login);
        file.FileUrl = url;
        return file;
    }

    [Fact]
    public void CollectExportRows_ExpandsPackages_DedupesChildren_KeepsOnlyCompletedWithUrl()
    {
        (Package pkg, FileHosterClient h, FileHosterLoginDto l) = MakePackage();
        PackageFile done = MakeFile(pkg, h, l, @"C:\d\a.r00", "https://rg/a0");
        PackageFile noUrl = MakeFile(pkg, h, l, @"C:\d\a.r01", null);          // completed but URL-less
        PackageFile queued = MakeFile(pkg, h, l, @"C:\d\a.r02", "https://x");  // not completed
        pkg.AddPackageFiles(new[] { done, noUrl, queued });
        done.State = FileState.Completed;
        noUrl.State = FileState.Completed;
        queued.State = FileState.UploadQueued;

        (Package other, FileHosterClient h2, FileHosterLoginDto l2) = MakePackage();
        PackageFile loose = MakeFile(other, h2, l2, @"C:\d\b.bin", "https://kf/b");
        other.AddPackageFiles(new[] { loose });
        loose.State = FileState.Completed;

        // Selection: the package, its own child again (must not double), and a loose completed file.
        List<LinkExportRow> rows = UploadsViewModel.CollectExportRows([pkg, done, loose]);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new LinkExportRow("a.r00", "TestHost", "https://rg/a0"), rows[0]);
        Assert.Equal(new LinkExportRow("b.bin", "TestHost", "https://kf/b"), rows[1]);
    }

    [Fact]
    public void TryBuildExplorerSelectArgument_ExistingFile_ReturnsQuotedSelectArgument()
    {
        string full = Path.Combine(@"C:\src\My Uploads", "movie.mkv");

        string? arg = UploadsViewModel.TryBuildExplorerSelectArgument(@"C:\src\My Uploads", "movie.mkv", p => p == full);

        // /select,"<path>" highlights the file in its folder — comma form, path quoted (handles spaces).
        Assert.Equal($"/select,\"{full}\"", arg);
    }

    [Fact]
    public void TryBuildExplorerSelectArgument_MissingFile_ReturnsNull_SoCallerOpensFolder()
        => Assert.Null(UploadsViewModel.TryBuildExplorerSelectArgument(@"C:\src\pkg", "gone.bin", _ => false));

    [Theory]
    [InlineData(null, "f.bin")]
    [InlineData("", "f.bin")]
    [InlineData(@"C:\d", null)]
    [InlineData(@"C:\d", "")]
    public void TryBuildExplorerSelectArgument_MissingDirectoryOrName_ReturnsNull(string? directory, string? fileName)
        => Assert.Null(UploadsViewModel.TryBuildExplorerSelectArgument(directory, fileName, _ => true));
}
