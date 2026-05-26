// <copyright file="FlashBitPipelineSmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;
using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Config sanity check for <see cref="FlashBitPipeline"/>. Protocol coverage lives in
/// <see cref="ExLoadPipelineTests"/> + <see cref="XFileSharingApiPipelineSubclassTests"/>
/// — pin the per-subclass config so we don't ship a copy-paste with the wrong host.
/// </summary>
public class FlashBitPipelineSmokeTests
{
    [Fact]
    public void Properties_DeclareFlashBitConfigAndStandardFreeTierLimits()
    {
        FlashBitPipeline pipeline = new();

        Assert.Equal("FlashBit", pipeline.Name);
        Assert.Equal(1L * 1024 * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Equal(30, pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
    }

    [Fact]
    public void Name_MatchesFileHostersRegistryKey()
    {
        FlashBitPipeline pipeline = new();
        Assert.True(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }
}
