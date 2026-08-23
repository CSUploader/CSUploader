// <copyright file="PutChunkPartProgressTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using System.Net.Http;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using Moq;

namespace CSUploader.Tests.Lib.Net.Http;

/// <summary>
/// <c>PutChunkAsync</c>'s two progress modes, driven through a real <see cref="HttpHandler"/>.
/// <para>
/// The pipeline tests exercise the progress path through their own test seam, which means deleting
/// <c>reportPartProgress:</c> from a pipeline's production call would leave every one of them green.
/// These pin the branch itself: what the hook receives, and — just as important — that the ordinary
/// absolute <c>UploadProgress</c> is NOT also raised when the hook is supplied, since publishing
/// both would interleave a per-part figure with the aggregator's file-wide ones.
/// </para>
/// </summary>
public class PutChunkPartProgressTests
{
    private const int ChunkBytes = 40_000;
    private const long BasePosition = 100_000;
    private const long TotalFileSize = 500_000;

    private static (HttpHandler Handler, List<long> Absolute) BuildHandler()
    {
        HttpHandler handler = new(
            new HttpClient(new DrainingHandler()), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);

        List<long> absolute = [];
        handler.UploadProgress += (_, e) => absolute.Add(e.BytesProcessed);
        return (handler, absolute);
    }

    [Fact]
    public async Task PutChunkAsync_WithAReporter_ReportsCumulativeBytesWithinThePart()
    {
        (HttpHandler handler, List<long> absolute) = BuildHandler();
        List<long> reported = [];

        using (handler)
        {
            await handler.PutChunkAsync(
                "https://example.invalid/part",
                new MemoryStream(new byte[ChunkBytes]),
                ChunkBytes,
                BasePosition,
                TotalFileSize,
                DateTime.Now,
                SpeedBudget.Unlimited,
                headers: null,
                CancellationToken.None,
                method: null,
                reportPartProgress: reported.Add);
        }

        Assert.NotEmpty(reported);

        // Cumulative WITHIN the part: it must end at the chunk length, never at
        // basePosition + length, and never be a per-read delta.
        Assert.Equal(ChunkBytes, reported[^1]);
        Assert.Equal(reported.OrderBy(x => x), reported);
        Assert.All(reported, value => Assert.InRange(value, 1, ChunkBytes));
    }

    [Fact]
    public async Task PutChunkAsync_WithAReporter_DoesNotAlsoRaiseTheAbsoluteEvent()
    {
        // The aggregator owns publication for a parallel upload. Raising here too would put a
        // per-part figure into the same stream as the file-wide totals.
        (HttpHandler handler, List<long> absolute) = BuildHandler();

        using (handler)
        {
            await handler.PutChunkAsync(
                "https://example.invalid/part",
                new MemoryStream(new byte[ChunkBytes]),
                ChunkBytes,
                BasePosition,
                TotalFileSize,
                DateTime.Now,
                SpeedBudget.Unlimited,
                headers: null,
                CancellationToken.None,
                method: null,
                reportPartProgress: _ => { });
        }

        Assert.Empty(absolute);
    }

    [Fact]
    public async Task PutChunkAsync_WithoutAReporter_RaisesTheAbsoluteEvent_AsBefore()
    {
        // The sequential path is unchanged: basePosition + bytesInThisChunk, so the UI sees one
        // monotonic file-cumulative stream.
        (HttpHandler handler, List<long> absolute) = BuildHandler();

        using (handler)
        {
            await handler.PutChunkAsync(
                "https://example.invalid/part",
                new MemoryStream(new byte[ChunkBytes]),
                ChunkBytes,
                BasePosition,
                TotalFileSize,
                DateTime.Now,
                SpeedBudget.Unlimited,
                headers: null,
                CancellationToken.None);
        }

        Assert.NotEmpty(absolute);
        Assert.Equal(BasePosition + ChunkBytes, absolute[^1]);
        Assert.All(absolute, value => Assert.InRange(value, BasePosition + 1, BasePosition + ChunkBytes));
    }

    /// <summary>Reads the whole request body and answers 200, so the body genuinely streams and
    /// progress is raised for real rather than short-circuited.</summary>
    private sealed class DrainingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                await request.Content.CopyToAsync(Stream.Null, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
    }
}
