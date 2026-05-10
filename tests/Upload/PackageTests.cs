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
        string tempA = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string tempB = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
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

            Assert.Equal(2, package.Count());
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
}
