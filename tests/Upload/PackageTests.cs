// <copyright file="PackageTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using Moq;

namespace CSUploader.Tests.Upload;

public class PackageTests
{
    [Fact]
    public void AddPackageFiles_NoArg_AddsOneFilePerSelectedFilePerHoster()
    {
        string tempA = Path.Combine(Path.GetTempPath(), "task1-a.bin");
        string tempB = Path.Combine(Path.GetTempPath(), "task1-b.bin");
        File.WriteAllText(tempA, "a");
        File.WriteAllText(tempB, "b");
        try
        {
            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            PackageOptions options = new()
            {
                Title = "test",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [tempA, tempB],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };
            Package package = new(options);

            package.AddPackageFiles();

            PackageFile[] files = [.. package];
            Assert.Equal(2, files.Length);
            Assert.Contains(files, f => string.Equals(f.Name, Path.GetFileName(tempA), StringComparison.Ordinal));
            Assert.Contains(files, f => string.Equals(f.Name, Path.GetFileName(tempB), StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(tempA);
            File.Delete(tempB);
        }
    }

    [Fact]
    public void AddPackageFiles_NoArg_WhenSelectedFilesEmpty_AddsNothing()
    {
        FileHosterClient hoster = new("Rapidgator", Protocol.Http);
        PackageOptions options = new()
        {
            Title = "test",
            Logger = Mock.Of<IAppLogger>(),
            SelectedFiles = [],
            FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
        };
        Package package = new(options);

        package.AddPackageFiles();

        Assert.Empty(package);
    }

    [Fact]
    public void AddPackageFiles_NoArg_WhenSelectedFilesNull_AddsNothing()
    {
        FileHosterClient hoster = new("Rapidgator", Protocol.Http);
        PackageOptions options = new()
        {
            Title = "test",
            Logger = Mock.Of<IAppLogger>(),
            SelectedFiles = null,
            FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
        };
        Package package = new(options);

        package.AddPackageFiles();

        Assert.Empty(package);
    }
}
