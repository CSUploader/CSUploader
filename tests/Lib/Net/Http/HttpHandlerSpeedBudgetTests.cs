// <copyright file="HttpHandlerSpeedBudgetTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using System.Net.Http;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using CSUploader.Tests.TestSupport;
using Moq;

namespace CSUploader.Tests.Lib.Net.Http;

/// <summary>
/// The last link: that a budget handed to <see cref="HttpHandler"/> actually governs the request
/// body it sends.
/// <para>
/// This needs its own test because every budget parameter on <c>HttpHandler</c> is OPTIONAL — a call
/// site that simply forgets to pass one compiles cleanly and silently uploads unthrottled. The
/// bucket tests prove the arithmetic and the scope tests prove the resolution, but neither would
/// notice that join being dropped.
/// </para>
/// </summary>
public class HttpHandlerSpeedBudgetTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"csu-budget-{Guid.NewGuid():N}.bin");

    public HttpHandlerSpeedBudgetTests() => File.WriteAllBytes(_path, new byte[4096]);

    public void Dispose()
    {
        File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static long Remaining(SpeedLimiter limiter)
    {
        long total = 0;
        while (true)
        {
            SpeedReservation r = limiter.TryAcquire(int.MaxValue);
            if (r.Bytes == 0)
            {
                return total;
            }

            total += r.Bytes;
        }
    }

    [Fact]
    public async Task UploadMultipartAsync_ChargesTheBudgetItWasGiven()
    {
        // A limit high enough that nothing waits — this asserts the JOIN, not the rate.
        ManualTimeProvider clock = new();
        SpeedLimiter limiter = new(() => 10_000_000, clock);
        clock.Advance(TimeSpan.FromMilliseconds(100)); // capacity = 1,000,000

        using HttpClient client = new(new DrainingHandler());
        using HttpHandler handler = new(client, Mock.Of<IAppLogger>(), proxyDescription: null, MockServerConfig.Disabled);

        await handler.UploadMultipartAsync(
            _path,
            "https://example.invalid/upload",
            "file",
            speedBudget: new SpeedBudget(() => limiter));

        // Exactly the file's bytes: the short read and the EOF probe both refund what they did not
        // move, so an over-charge here would mean a missing refund and an under-charge would mean
        // the body bypassed the budget.
        Assert.Equal(1_000_000 - 4096, Remaining(limiter));
    }

    [Fact]
    public async Task UploadFileAsync_ChargesTheBudgetItWasGiven()
    {
        // The other upload entry point, which takes the budget positionally — a different call
        // shape, and the one Alfafile and Rapidgator use.
        ManualTimeProvider clock = new();
        SpeedLimiter limiter = new(() => 10_000_000, clock);
        clock.Advance(TimeSpan.FromMilliseconds(100));

        using HttpClient client = new(new DrainingHandler());
        using HttpHandler handler = new(client, Mock.Of<IAppLogger>(), proxyDescription: null, MockServerConfig.Disabled);

        await handler.UploadFileAsync(_path, "https://example.invalid/upload", new SpeedBudget(() => limiter));

        Assert.Equal(1_000_000 - 4096, Remaining(limiter));
    }

    [Fact]
    public async Task UploadMultipartAsync_WithNoBudget_UploadsUnthrottled()
    {
        // The documented meaning of omitting it, pinned so the optional parameter is a choice
        // rather than an accident.
        using HttpClient client = new(new DrainingHandler());
        using HttpHandler handler = new(client, Mock.Of<IAppLogger>(), proxyDescription: null, MockServerConfig.Disabled);

        HttpResponseSnapshot response = await handler.UploadMultipartAsync(
            _path, "https://example.invalid/upload", "file");

        Assert.Equal(200, response.StatusCode);
    }

    /// <summary>Reads the whole request body and answers 200, so the body genuinely streams through
    /// the throttle rather than being short-circuited.</summary>
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
