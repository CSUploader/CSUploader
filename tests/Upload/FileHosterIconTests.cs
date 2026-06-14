// <copyright file="FileHosterIconTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Runtime.CompilerServices;
using CSUploader.Upload;

namespace CSUploader.Tests.Upload;

public class FileHosterIconTests
{
    [Fact]
    public void EveryHoster_HasAMatchingIconAsset()
    {
        // The Add-Account / wizard icon converter resolves "filehoster_<name>.png" (name
        // lowercased, spaces and hyphens stripped) and silently falls back to text when the
        // asset is missing — so a forgotten icon ships as a blank cell with no error. Pin the
        // invariant: every hoster in the master registry must have an icon asset. (This gap
        // shipped for Hexload/Hotlink/Hxfile, added without their PNGs.)
        string imagesDir = Path.Combine(RepoRoot(), "src", "Properties", "Images", "FileHosters");

        List<string> missing = [];
        foreach (string name in FileHosterClient.NamesAlphabetical)
        {
            string normalized = name.ToLowerInvariant()
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal);
            if (!File.Exists(Path.Combine(imagesDir, $"filehoster_{normalized}.png")))
            {
                missing.Add(name);
            }
        }

        Assert.True(
            missing.Count == 0,
            "Hosters missing an icon asset (src/Properties/Images/FileHosters/filehoster_<name>.png): " + string.Join(", ", missing));
    }

    // Walk up from THIS test's source file to the repo root (the folder holding
    // CSUploader.sln). Using the compile-time source path (not the build output) keeps the
    // test correct even when the assembly is built to a redirected output dir, and it checks
    // the source tree directly — the icons live in the src project, embedded as WPF resources.
    private static string RepoRoot([CallerFilePath] string thisFilePath = "")
    {
        DirectoryInfo? dir = Directory.GetParent(thisFilePath);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CSUploader.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (CSUploader.sln) from " + thisFilePath);
    }
}
