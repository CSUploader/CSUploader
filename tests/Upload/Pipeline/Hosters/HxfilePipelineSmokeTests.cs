// <copyright file="HxfilePipelineSmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;
using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>Config sanity check for <see cref="HxfilePipeline"/>. Same shape as the other
/// XFS-API smoke tests.</summary>
public class HxfilePipelineSmokeTests
{
    [Fact]
    public void Properties_DeclareHxfileConfigAndStandardFreeTierLimits()
    {
        HxfilePipeline pipeline = new();
        Assert.Equal("Hxfile", pipeline.Name);
        Assert.Equal(1L * 1024 * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
    }

    [Fact]
    public void Name_MatchesFileHostersRegistryKey()
    {
        HxfilePipeline pipeline = new();
        Assert.True(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }
}
