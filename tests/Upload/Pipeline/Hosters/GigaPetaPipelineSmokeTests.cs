// <copyright file="GigaPetaPipelineSmokeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>Config sanity check for <see cref="GigaPetaPipeline"/> — anonymous-only for now.</summary>
public class GigaPetaPipelineSmokeTests
{
    [Fact]
    public void Properties_DeclareGigaPetaAnonymousConfig()
    {
        GigaPetaPipeline pipeline = new();
        Assert.Equal("GigaPeta", pipeline.Name);
        // 250 MiB — the anonymous upload form's hard MAX_FILE_SIZE.
        Assert.Equal(262144000L, pipeline.MaxFileSize);
        Assert.Null(pipeline.MaxFilesPerPackage);
        Assert.False(pipeline.RequiresHashingBeforeUpload);
        Assert.False(pipeline.RequiresHashingAfterUpload);
        Assert.True(pipeline.SupportsAnonymousUpload);
    }

    [Fact]
    public void Name_MatchesFileHostersRegistryKey()
    {
        GigaPetaPipeline pipeline = new();
        Assert.True(FileHosterClient.FileHosters.ContainsKey(pipeline.Name));
    }

    [Fact]
    public async Task CheckAccountAsync_LoginNotSupportedYet_ReturnsClearFailure()
    {
        // GigaPeta is anonymous-only for now; a login attempt must fail with a message that
        // points the user at the Anonymous option rather than silently erroring.
        GigaPetaPipeline pipeline = new();

        AccountCheckResult result = await pipeline.CheckAccountAsync(
            "user", "pass", apiKey: null,
            new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled),
            ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("Anonymous", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
