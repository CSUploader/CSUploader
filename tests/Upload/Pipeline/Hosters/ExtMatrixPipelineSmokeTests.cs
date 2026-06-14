// <copyright file="ExtMatrixPipelineSmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;
using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// Config sanity check for <see cref="ExtMatrixPipeline"/>. ExtMatrix has its own bespoke
/// REST API (NOT XFileSharingPro), and the hoster-specific 250 MiB cap + null files-per-
/// package come from their docs (see the pipeline's class-level remarks for context).
/// </summary>
/// <remarks>
/// ExtMatrix is currently DISABLED (see ExtMatrixPipeline.cs class-level remarks for the
/// reason and re-enable checklist). The class is intentionally retained so the day
/// ExtMatrix's infrastructure is fixed we can re-enable with minimal churn; these tests
/// keep that retention honest — properties still compile, and we explicitly assert
/// that the hoster is ABSENT from the registry so an accidental re-add of the
/// FileHosterClient entry without re-enabling everywhere else gets caught by the suite.
/// </remarks>
public class ExtMatrixPipelineSmokeTests
{
    [Fact]
    public void Properties_DeclareExtMatrixConfigAndFreeTierLimits()
    {
        ExtMatrixPipeline pipeline = new();
        Assert.Equal("ExtMatrix", pipeline.Name);
        Assert.Equal(250L * 1024 * 1024, pipeline.MaxFileSize);
        // No batch cap — protocol is single-POST-per-file, so an N-file package just
        // means N independent POSTs. The "1 simultaneous" the user mentioned is a
        // concurrency throttle (not yet implemented); see pipeline remarks.
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
    }

    [Fact]
    public void Name_IsNotRegistered_WhileDisabled()
    {
        // Sentinel: if this assertion ever starts failing, someone re-added "ExtMatrix"
        // to FileHosterClient.FileHosters. Before flipping it to Assert.True, walk
        // through ExtMatrixPipeline.cs's "Re-enable checklist" — the FileHosters entry
        // is only one of four touchpoints (DI registration + ApiKeyHosters + this test
        // also need to flip, and the underlying nginx body-cap / chunked-protocol issue
        // must be resolved upstream).
        ExtMatrixPipeline pipeline = new();
        Assert.False(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }
}
