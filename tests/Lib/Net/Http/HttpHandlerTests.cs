// <copyright file="HttpHandlerTests.cs" company="CSUploader">
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
/// Verifies that <see cref="HttpHandler"/> stamps every captured <see cref="HttpTransaction"/>
/// with the proxy description it was constructed with — that's how the Logs tab can show
/// users which proxy a request went through. Uses a stub <see cref="HttpMessageHandler"/>
/// so the tests don't need a network.
/// </summary>
public class HttpHandlerTests
{
    [Fact]
    public async Task GetStringAsync_NullProxyDescription_LogsTransactionAsDirect()
    {
        TransactionCapture capture = new();
        HttpClient client = StubClient(HttpStatusCode.OK, "ok");
        HttpHandler handler = new(client, capture.Logger, proxyDescription: null, MockServerConfig.Disabled);

        await handler.GetStringAsync("https://example.test/x");

        Assert.NotNull(capture.Transaction);
        Assert.Equal("(direct)", capture.Transaction!.Proxy);
    }

    [Fact]
    public async Task GetStringAsync_EmptyProxyDescription_LogsTransactionAsDirect()
    {
        // Defensive: callers that pass "" (rather than null) should also fall back to "(direct)".
        TransactionCapture capture = new();
        HttpClient client = StubClient(HttpStatusCode.OK, "ok");
        HttpHandler handler = new(client, capture.Logger, proxyDescription: string.Empty, MockServerConfig.Disabled);

        await handler.GetStringAsync("https://example.test/x");

        Assert.Equal("(direct)", capture.Transaction!.Proxy);
    }

    [Fact]
    public async Task GetStringAsync_WithProxyDescription_LogsTransactionWithThatDescription()
    {
        TransactionCapture capture = new();
        HttpClient client = StubClient(HttpStatusCode.OK, "ok");
        HttpHandler handler = new(client, capture.Logger, "socks5://10.0.0.1:1080", MockServerConfig.Disabled);

        await handler.GetStringAsync("https://example.test/x");

        Assert.Equal("socks5://10.0.0.1:1080", capture.Transaction!.Proxy);
    }

    [Fact]
    public async Task GetStringAsync_WhenRequestThrows_StillLogsTransactionWithProxyDescription()
    {
        // Failures are exactly the case where the proxy description matters most — a glance
        // at the Logs tab needs to point at the right proxy. The catch-block logging path
        // must carry the description as well.
        TransactionCapture capture = new();
        HttpClient client = ThrowingClient(new HttpRequestException("boom"));
        HttpHandler handler = new(client, capture.Logger, "http://1.2.3.4:8080", MockServerConfig.Disabled);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => handler.GetStringAsync("https://example.test/x"));

        Assert.NotNull(capture.Transaction);
        Assert.Equal("http://1.2.3.4:8080", capture.Transaction!.Proxy);
    }

    private static HttpClient StubClient(HttpStatusCode status, string body) =>
        new(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
            ReasonPhrase = status.ToString(),
        })));

    private static HttpClient ThrowingClient(Exception ex) =>
        new(new StubHandler((_, _) => Task.FromException<HttpResponseMessage>(ex)));

    /// <summary>
    /// Captures the first <see cref="HttpTransaction"/> passed to <see cref="IAppLogger.Log"/>
    /// with <see cref="LogType.Http"/>. Status logs (e.g. the mock-server-disabled message)
    /// are ignored because they pass <c>null</c> for the transaction.
    /// </summary>
    private sealed class TransactionCapture
    {
        public HttpTransaction? Transaction { get; private set; }

        public IAppLogger Logger { get; }

        public TransactionCapture()
        {
            Mock<IAppLogger> mock = new();
            mock.Setup(l => l.Log(
                    It.IsAny<object?>(),
                    It.IsAny<LogType>(),
                    It.IsAny<string>(),
                    It.IsAny<HttpTransaction?>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>()))
                .Callback<object?, LogType, string, HttpTransaction?, string, string, int>(
                    (_, _, _, tx, _, _, _) =>
                    {
                        if (tx is not null && Transaction is null)
                        {
                            Transaction = tx;
                        }
                    });
            Logger = mock.Object;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => impl(request, cancellationToken);
    }
}
