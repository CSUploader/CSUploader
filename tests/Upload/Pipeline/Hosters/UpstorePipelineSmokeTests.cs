// <copyright file="UpstorePipelineSmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;
using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>Config sanity check for <see cref="UpstorePipeline"/>. Upload-protocol coverage lives in
/// <see cref="UpstorePipelineUploadTests"/>.</summary>
public class UpstorePipelineSmokeTests
{
    [Fact]
    public void Properties_DeclareUpstoreConfigAndAnonymousGuestCap()
    {
        UpstorePipeline pipeline = new();

        Assert.Equal("Upstore", pipeline.Name);
        Assert.Equal(1L * 1024 * 1024 * 1024, pipeline.MaxFileSize); // 1 GiB free/guest cap (server: Error (Size1gb))

        // Per-tier caps: premium 5 GB (decimal, advertised figure), everything else the free/guest 1 GiB.
        Assert.Equal(5L * 1000 * 1000 * 1000, pipeline.MaxFileSizeFor(
            new CSUploader.Dal.FileHosterLoginDto { FileHosterName = "Upstore", AccountType = CSUploader.Upload.AccountType.Premium }));
        Assert.Equal(1L * 1024 * 1024 * 1024, pipeline.MaxFileSizeFor(
            new CSUploader.Dal.FileHosterLoginDto { FileHosterName = "Upstore", IsAnonymous = true }));
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.True(pipeline.SupportsAnonymousUpload);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
    }

    [Fact]
    public void Name_MatchesFileHostersRegistryKey()
    {
        UpstorePipeline pipeline = new();
        Assert.True(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }
}
