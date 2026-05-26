// <copyright file="TakeFilePipelineSmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;
using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Config sanity check for <see cref="TakeFilePipeline"/>. Same shape as the FlashBit
/// / KatFile smoke tests.
/// </summary>
public class TakeFilePipelineSmokeTests
{
    [Fact]
    public void Properties_DeclareTakeFileConfigAndStandardFreeTierLimits()
    {
        TakeFilePipeline pipeline = new();

        Assert.Equal("TakeFile", pipeline.Name);
        Assert.Equal(1L * 1024 * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Equal(30, pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
    }

    [Fact]
    public void Name_MatchesFileHostersRegistryKey()
    {
        TakeFilePipeline pipeline = new();
        Assert.True(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }
}
