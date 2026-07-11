// <copyright file="FlashBitPipelineSmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;
using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Config sanity check for <see cref="FlashBitPipeline"/>. Protocol coverage lives in
/// <see cref="ExLoadPipelineTests"/> + <see cref="XFileSharingApiPipelineSubclassTests"/>.
/// </summary>
/// <remarks>
/// FlashBit is currently DISABLED (see FlashBitPipeline.cs class-level remarks for the
/// reason and re-enable checklist). The class is intentionally retained so the day
/// FlashBit's infrastructure is fixed we can re-enable with minimal churn; these tests
/// keep that retention honest — properties still compile, and we explicitly assert
/// that the hoster is ABSENT from the registry so an accidental re-add of the
/// FileHosterClient entry without re-enabling everywhere else gets caught by the suite.
/// </remarks>
public class FlashBitPipelineSmokeTests
{
    [Fact]
    public void Properties_DeclareFlashBitConfigAndStandardFreeTierLimits()
    {
        FlashBitPipeline pipeline = new();
        Assert.Equal("FlashBit", pipeline.Name);
        Assert.Equal(1L * 1024 * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
    }

    [Fact]
    public void Name_IsNotRegistered_WhileDisabled()
    {
        // Sentinel: if this assertion ever starts failing, someone re-added "FlashBit"
        // to FileHosterClient.FileHosters. Before flipping it to Assert.True, walk
        // through FlashBitPipeline.cs's "Re-enable checklist" — the FileHosters entry
        // is only one of four touchpoints (DI registration + HosterCredentialModes.ApiKeyHosters
        // in src/CSUploader.Core/Upload/HosterCredentialModes.cs + this test also need to flip, and
        // the underlying SSL / IIS issues must be verified resolved upstream).
        FlashBitPipeline pipeline = new();
        Assert.False(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }
}
