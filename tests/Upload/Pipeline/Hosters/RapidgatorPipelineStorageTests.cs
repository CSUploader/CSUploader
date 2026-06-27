// <copyright file="RapidgatorPipelineStorageTests.cs" company="CSUploader">
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

public class RapidgatorPipelineStorageTests
{
    [Theory]
    [InlineData(1000L, 400L, 600L, 1000L)]   // normal: used = total - left
    [InlineData(1000L, 1000L, 0L, 1000L)]    // nothing stored yet → used 0
    [InlineData(1000L, 0L, 1000L, 1000L)]    // full → used == quota
    [InlineData(1000L, null, null, 1000L)]   // remaining unknown → quota only, no used
    [InlineData(1000L, 1200L, null, 1000L)]  // left > total (implausible) → quota only
    [InlineData(1000L, -1L, null, 1000L)]    // negative remaining → quota only
    [InlineData(null, 400L, null, null)]     // no total → nothing
    [InlineData(-5L, 400L, null, null)]      // negative total → nothing
    public void MapStorage_DerivesUsedFromTotalMinusLeft(long? total, long? left, long? expectedUsed, long? expectedQuota)
    {
        (long? used, long? quota) = RapidgatorPipeline.MapStorage(total, left);

        Assert.Equal(expectedUsed, used);
        Assert.Equal(expectedQuota, quota);
    }

    [Fact]
    public async Task RefreshStorageAsync_ValidLogin_ReturnsUsedAndQuota()
    {
        // left given as a STRING here too, to prove AllowReadingFromString covers both fields.
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK","user":{"email":"u@example.com","is_premium":false,"premium_end_time":null,"folder_id":1,"storage":{"total":"1000","left":"400"}}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        StorageUsage? usage = await pipeline.RefreshStorageAsync(Creds(), MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.NotNull(usage);
        Assert.Equal(600, usage!.Value.UsedBytes);
        Assert.Equal(1000, usage.Value.QuotaBytes);
    }

    [Fact]
    public async Task RefreshStorageAsync_BadCredentials_ReturnsNull()
    {
        Queue<string> responses = new(new[]
        {
            """{"response":null,"status":401,"details":"Login or password is wrong"}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        StorageUsage? usage = await pipeline.RefreshStorageAsync(Creds(), MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.Null(usage);
    }

    [Fact]
    public async Task RefreshStorageAsync_NoStorageBlock_ReturnsNull()
    {
        // Valid login but no storage block → "couldn't refresh", so the caller keeps its snapshot.
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK","user":{"email":"u@example.com","is_premium":false,"premium_end_time":null,"folder_id":1}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        StorageUsage? usage = await pipeline.RefreshStorageAsync(Creds(), MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.Null(usage);
    }

    [Fact]
    public async Task RefreshStorageAsync_UnexpectedBody_ReturnsNull()
    {
        Queue<string> responses = new(new[] { "<html>nope</html>" });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        StorageUsage? usage = await pipeline.RefreshStorageAsync(Creds(), MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.Null(usage);
    }

    [Fact]
    public async Task RefreshStorageAsync_Cancelled_Propagates()
    {
        // The interface contract: OperationCanceledException is the ONLY exception that propagates
        // (all other failures collapse to null). AccountVerifier relies on it to rethrow.
        RapidgatorPipeline pipeline = new((Func<string, string>)(url => throw new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => pipeline.RefreshStorageAsync(Creds(), MakeHandler(), ProxyChoice.Direct, CancellationToken.None));
    }

    private static FileHosterLoginDto Creds()
        => new() { FileHosterName = "Rapidgator", Username = "u@example.com", Password = "secret" };

    private static HttpHandler MakeHandler()
        => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
}
