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

    [Fact]
    public void SaveFrom_NoFiles_ReturnsNull()
    {
        Package package = new(new PackageOptions { Title = "t", FileHosters = new() });

        Assert.Null(package.SaveFrom);
    }

    [Fact]
    public void SaveFrom_AllFilesShareDirectory_ReturnsThatDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string a = Path.Combine(dir, "a.bin");
        string b = Path.Combine(dir, "b.bin");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        try
        {
            PackageOptions options = new()
            {
                Title = "t",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [a, b],
                FileHosters = new() { { new FileHosterClient("Rapidgator", Protocol.Http), new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };
            Package package = new(options);
            package.AddPackageFiles();

            Assert.Equal(dir, package.SaveFrom);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SaveFrom_FilesShareAncestor_ReturnsLongestCommonParent()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        string topFile = Path.Combine(root, "top.bin");
        string subFile = Path.Combine(sub, "child.bin");
        File.WriteAllText(topFile, "x");
        File.WriteAllText(subFile, "y");
        try
        {
            PackageOptions options = new()
            {
                Title = "t",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [topFile, subFile],
                FileHosters = new() { { new FileHosterClient("Rapidgator", Protocol.Http), new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            };
            Package package = new(options);
            package.AddPackageFiles();

            Assert.Equal(root, package.SaveFrom);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(new string?[] { @"C:\X\Y", @"C:\X\Z" }, @"C:\X")]
    [InlineData(new string?[] { @"C:\X\Y\Inner", @"C:\X\Y\Other" }, @"C:\X\Y")]
    [InlineData(new string?[] { @"C:\X" }, @"C:\X")]
    [InlineData(new string?[] { @"C:\X\Y", @"D:\Z" }, null)]
    [InlineData(new string?[] { @"C:\X\Y", null }, @"C:\X\Y")]
    [InlineData(new string?[] { null, null }, null)]
    [InlineData(new string?[] { }, null)]
    public void LongestCommonDirectory_Cases(string?[] inputs, string? expected)
    {
        Assert.Equal(expected, Package.LongestCommonDirectory(inputs));
    }
}
