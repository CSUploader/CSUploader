// <copyright file="PackageFilePipelineEventsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload;

public class PackageFilePipelineEventsTests
{
    [Fact]
    public void ApplyEvent_TransferProgress_UpdatesProgressFields()
    {
        PackageFile file = MakeFile(out _);

        file.ApplyEvent(new TransferProgress(BytesUploaded: 50, TotalBytes: 100, SpeedBytesPerSec: 1024));

        Assert.Equal(50, file.BytesLoaded);
        Assert.Equal(50, file.BytesRemaining);
        Assert.Equal(50.0, file.Progress);
    }

    [Fact]
    public void ApplyEvent_TransferCompleted_SetsFinishedAndUrl()
    {
        PackageFile file = MakeFile(out _);

        file.ApplyEvent(new TransferCompleted("https://x/y"));

        Assert.True(file.IsUploadFinished);
        Assert.Equal("https://x/y", file.FileUrl);
        Assert.Equal(100.0, file.Progress);
    }

    [Fact]
    public void ApplyEvent_AttemptFailed_SetsError()
    {
        PackageFile file = MakeFile(out _);

        file.ApplyEvent(new AttemptFailed("network down", null));

        Assert.Equal("network down", file.Error);
    }

    private static PackageFile MakeFile(out FileHosterClient client)
    {
        // Use any tempfile path that exists for the FileInfo construction
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "x");
        Package pkg = new(new PackageOptions { DirectoryPath = Path.GetDirectoryName(path)!, Logger = Mock.Of<IAppLogger>() });
        client = new FileHosterClient("Stub", Protocol.Http);
        PackageFile file = new(pkg, path, client, new FileHosterLoginDto());
        return file;
    }
}
