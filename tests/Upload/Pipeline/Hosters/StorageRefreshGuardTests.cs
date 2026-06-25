// <copyright file="StorageRefreshGuardTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

/// <summary>
/// The session-required guards on the WebView-free storage refresh: FileBoom and HitFile re-read
/// usage through a stored session, so with none they must return null (caller keeps the snapshot)
/// without touching the network.
/// </summary>
public class StorageRefreshGuardTests
{
    private static HttpHandler Handler() => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

    [Fact]
    public async Task FileBoom_RefreshStorage_NoStoredSession_ReturnsNull()
    {
        IStorageRefreshablePipeline pipeline = new FileBoomPipeline();

        StorageUsage? usage = await pipeline.RefreshStorageAsync(
            new FileHosterLoginDto { FileHosterName = "FileBoom", SessionCookie = null },
            Handler(),
            ProxyChoice.Direct,
            CancellationToken.None);

        Assert.Null(usage);
    }

    [Fact]
    public async Task FileBoom_RefreshStorage_ExpiredSession_ReturnsNull()
    {
        // A stored-but-expired JWT must short-circuit to null BEFORE any network call (no refresh,
        // and definitely no WebView) — the persisted expiry is in the past.
        IStorageRefreshablePipeline pipeline = new FileBoomPipeline();

        StorageUsage? usage = await pipeline.RefreshStorageAsync(
            new FileHosterLoginDto
            {
                FileHosterName = "FileBoom",
                SessionCookie = "stale.jwt.token",
                SessionCookieExpiresUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            Handler(),
            ProxyChoice.Direct,
            CancellationToken.None);

        Assert.Null(usage);
    }

    [Fact]
    public async Task HitFile_RefreshStorage_NoStoredSession_ReturnsNull()
    {
        IStorageRefreshablePipeline pipeline = new HitFilePipeline();

        StorageUsage? usage = await pipeline.RefreshStorageAsync(
            new FileHosterLoginDto { FileHosterName = "HitFile", SessionCookie = null },
            Handler(),
            ProxyChoice.Direct,
            CancellationToken.None);

        Assert.Null(usage);
    }
}
