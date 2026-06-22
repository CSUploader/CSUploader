// <copyright file="HotlinkPipelineSmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;
using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>Config sanity check for <see cref="HotlinkPipeline"/>. Same shape as the other
/// XFS-API smoke tests.</summary>
public class HotlinkPipelineSmokeTests
{
    [Fact]
    public void Properties_DeclareHotlinkConfigAndUncappedFileSize()
    {
        HotlinkPipeline pipeline = new();
        Assert.Equal("Hotlink", pipeline.Name);

        // Uncapped, mirroring Ex-Load — the base's 1 GiB default was only a conservative
        // guess; lifted until a real free-account upload confirms the actual limit.
        Assert.Null(pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
    }

    [Fact]
    public void Name_MatchesFileHostersRegistryKey()
    {
        HotlinkPipeline pipeline = new();
        Assert.True(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }
}
