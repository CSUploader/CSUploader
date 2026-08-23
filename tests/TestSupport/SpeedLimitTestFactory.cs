// <copyright file="SpeedLimitTestFactory.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using Moq;

namespace CSUploader.Tests.TestSupport;

internal static class SpeedLimitTestFactory
{
    /// <summary>
    /// A package backed by REAL files in a fresh temp directory — <see cref="PackageFile"/> reads
    /// <c>FileInfo.Length</c> on construction, so the paths must exist. Takes the
    /// <see cref="AppSettings"/> rather than a limit value so two packages can share one global
    /// scope, which is what the override-exceeds-global case needs.
    /// </summary>
    internal static Package Package(AppSettings settings, int? packageLimitKBps, int fileCount = 2)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"csu-speed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        List<string> paths = [];
        for (int i = 0; i < fileCount; i++)
        {
            string path = Path.Combine(dir, $"f{i}.bin");
            File.WriteAllBytes(path, new byte[1024]);
            paths.Add(path);
        }

        PackageOptions options = new()
        {
            Title = "speed",
            Logger = Mock.Of<IAppLogger>(),
            Settings = settings,
            SelectedFiles = paths,
            FileHosters = new()
            {
                { new FileHosterClient("Catbox", Protocol.Http), new FileHosterLoginDto { FileHosterName = "Catbox" } },
            },
        };

        Package package = new(options) { SpeedLimitKBps = packageLimitKBps };
        package.AddPackageFiles();
        return package;
    }
}
