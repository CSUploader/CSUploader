// <copyright file="KatFilePipelineSmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;
using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Lightweight smoke test for <see cref="KatFilePipeline"/>. Full protocol coverage
/// already lives in <see cref="ExLoadPipelineTests"/> + <see cref="XFileSharingApiPipelineSubclassTests"/>
/// — since KatFile inherits from <see cref="XFileSharingApiPipeline"/> verbatim and only
/// overrides Name + Host, the only behaviour worth pinning per-subclass is the config
/// itself (so we don't accidentally ship a copy-paste with the wrong host).
/// </summary>
public class KatFilePipelineSmokeTests
{
    [Fact]
    public void Properties_DeclareKatFileConfigAndStandardFreeTierLimits()
    {
        KatFilePipeline pipeline = new();

        Assert.Equal("KatFile", pipeline.Name);
        // Inherits the standard XFileSharing free-tier defaults from the base.
        Assert.Equal(1L * 1024 * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
    }

    [Fact]
    public void Name_MatchesFileHostersRegistryKey()
    {
        // The name MUST round-trip through the FileHosters registry so the pipeline
        // resolves correctly from the hoster column in the upload wizard.
        KatFilePipeline pipeline = new();
        Assert.True(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }
}
