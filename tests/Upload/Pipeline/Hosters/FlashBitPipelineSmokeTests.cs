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
/// FlashBit is currently disabled (see the class XML on <see cref="FlashBitPipeline"/>);
/// the registry-key check is inverted to assert that fact, so accidentally re-adding
/// the registry entry without addressing the underlying upload failure trips the test.
/// </summary>
public class FlashBitPipelineSmokeTests
{
    [Fact]
    public void Properties_DeclareFlashBitConfigAndStandardFreeTierLimits()
    {
        // The class is kept intact for the eventual re-enable, so its config should
        // still match what a registered pipeline would need.
        FlashBitPipeline pipeline = new();

        Assert.Equal("FlashBit", pipeline.Name);
        Assert.Equal(1L * 1024 * 1024 * 1024, pipeline.MaxFileSize);
        Assert.Equal(30, pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
    }

    [Fact]
    public void Name_IsNotRegistered_WhileDisabled()
    {
        // FlashBit is intentionally absent from FileHosterClient.FileHosters while the
        // storage-subdomain TLS issue + mid-upload connection drop are unresolved (see
        // FlashBitPipeline.cs's class XML for the diagnosis chain and the re-enable
        // checklist). Re-enabling FlashBit will trip this assertion — at that point,
        // flip it back to Assert.True with a matching update to the test name.
        FlashBitPipeline pipeline = new();
        Assert.False(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }
}
