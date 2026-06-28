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
/// <remarks>
/// TakeFile is currently DISABLED (see TakeFilePipeline.cs class-level remarks for the reason —
/// a Cloudflare managed-challenge TLS wall — and the re-enable checklist). The class is retained
/// so re-enabling is low-churn; these tests keep that retention honest (properties still compile)
/// and assert the hoster is ABSENT from the registry so an accidental re-add of the
/// FileHosterClient entry without re-enabling everywhere else gets caught by the suite.
/// </remarks>
public class TakeFilePipelineSmokeTests
{
    [Fact]
    public void Properties_DeclareTakeFileConfigAndStandardFreeTierLimits()
    {
        TakeFilePipeline pipeline = new();

        Assert.Equal("TakeFile", pipeline.Name);
        Assert.Equal(1L * 1024 * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
    }

    [Fact]
    public void Name_IsNotRegistered_WhileDisabled()
    {
        // Sentinel: if this assertion starts failing, someone re-added "TakeFile" to
        // FileHosterClient.FileHosters. Before flipping it to Assert.True, walk through
        // TakeFilePipeline.cs's re-enable checklist — the registry entry is only one of four
        // touchpoints (DI registration + ApiKeyHosters + this test also need to flip), and the
        // Cloudflare managed challenge must be confirmed gone upstream.
        TakeFilePipeline pipeline = new();
        Assert.False(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }
}
