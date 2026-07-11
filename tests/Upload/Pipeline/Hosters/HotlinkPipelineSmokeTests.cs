// <copyright file="HotlinkPipelineSmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;
using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Config sanity check for <see cref="HotlinkPipeline"/>. Hotlink is currently DISABLED (see
/// HotlinkPipeline.cs class-level remarks: free accounts can't upload + the per-user API key is
/// never rendered). The class is intentionally retained for cheap re-enable, and these tests keep
/// that retention honest — properties still compile, and we explicitly assert the hoster is ABSENT
/// from the registry so an accidental re-add of the FileHosterClient entry (without re-enabling
/// everywhere else) gets caught by the suite.
/// </summary>
public class HotlinkPipelineSmokeTests
{
    [Fact]
    public void Properties_DeclareHotlinkConfig()
    {
        HotlinkPipeline pipeline = new();
        Assert.Equal("Hotlink", pipeline.Name);
        Assert.Null(pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
    }

    [Fact]
    public void Name_IsNotRegistered_WhileDisabled()
    {
        // Sentinel: if this starts failing, someone re-added "Hotlink" to
        // FileHosterClient.FileHosters. Before flipping it to Assert.True, walk the re-enable
        // checklist in HotlinkPipeline.cs — the FileHosters entry is only one of four touchpoints
        // (DI registration + HosterCredentialModes.ApiKeyHosters in
        // src/CSUploader.Core/Upload/HosterCredentialModes.cs + this test also flip), and free accounts
        // still can't upload, so an upload-enabled account + the logged-in web-upload mode are prerequisites.
        HotlinkPipeline pipeline = new();
        Assert.False(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }
}
