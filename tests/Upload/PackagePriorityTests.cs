// <copyright file="PackagePriorityTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using System.IO;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using Moq;

namespace CSUploader.Tests.Upload;

public class PackagePriorityTests
{
    [Fact]
    public void Package_PriorityDefault_IsNormal()
    {
        Package package = new(new PackageOptions { Title = "p", FileHosters = new() });
        Assert.Equal(PackagePriority.Normal, package.Priority);
    }

    [Fact]
    public void Package_SettingPriority_RaisesPropertyChanged()
    {
        Package package = new(new PackageOptions { Title = "p", FileHosters = new() });
        List<string?> changes = [];
        package.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        package.Priority = PackagePriority.High;

        Assert.Contains(nameof(Package.Priority), changes);
    }

    [Fact]
    public void Package_SettingSamePriority_DoesNotRaisePropertyChanged()
    {
        Package package = new(new PackageOptions { Title = "p", FileHosters = new() });
        package.Priority = PackagePriority.High;
        List<string?> changes = [];
        package.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        package.Priority = PackagePriority.High;

        Assert.DoesNotContain(nameof(Package.Priority), changes);
    }

    [Fact]
    public void Package_WithNoFiles_ReturnsPriorityNormal()
    {
        // The old int? rollup returned null for empty packages and crashed XAML
        // bindings that didn't TargetNullValue. New backed property always has a value.
        Package package = new(new PackageOptions { Title = "p", FileHosters = new() });

        Assert.Equal(PackagePriority.Normal, package.Priority);
    }

    [Fact]
    public void Package_PriorityChange_CascadesToChildPackageFiles()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-prio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "a.bin");
            File.WriteAllText(filePath, "x");

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            Package package = new(new PackageOptions
            {
                Title = "p",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [filePath],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            });
            package.AddPackageFiles();
            PackageFile file = package.Single();

            List<string?> fileChanges = [];
            file.PropertyChanged += (_, e) => fileChanges.Add(e.PropertyName);

            package.Priority = PackagePriority.High;

            Assert.Contains(nameof(PackageFile.Priority), fileChanges);
            Assert.Equal(PackagePriority.High, file.Priority);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void PackageFile_Priority_PassesThroughToOwningPackage()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"csu-prio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "a.bin");
            File.WriteAllText(filePath, "x");

            FileHosterClient hoster = new("Rapidgator", Protocol.Http);
            Package package = new(new PackageOptions
            {
                Title = "p",
                Logger = Mock.Of<IAppLogger>(),
                SelectedFiles = [filePath],
                FileHosters = new() { { hoster, new FileHosterLoginDto { FileHosterName = "Rapidgator" } } },
            });
            package.AddPackageFiles();
            PackageFile file = package.Single();

            Assert.Equal(PackagePriority.Normal, file.Priority);
            package.Priority = PackagePriority.Lowest;
            Assert.Equal(PackagePriority.Lowest, file.Priority);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
