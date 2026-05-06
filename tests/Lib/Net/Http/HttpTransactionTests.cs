// <copyright file="HttpTransactionTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;

namespace CSUploader.Tests.Lib.Net.Http;

public class HttpTransactionTests
{
    [Fact]
    public void Proxy_DefaultsToDirect()
    {
        HttpTransaction tx = new();

        Assert.Equal("(direct)", tx.Proxy);
    }

    [Fact]
    public void Summary_IncludesProxyDescription()
    {
        HttpTransaction tx = new()
        {
            Method = "GET",
            Url = "https://example.com/api",
            StatusCode = 200,
            StatusReason = "OK",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddMilliseconds(123),
            Proxy = "http://10.0.0.1:8080",
        };

        Assert.Contains("[proxy: http://10.0.0.1:8080]", tx.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_DefaultProxy_RendersAsDirect()
    {
        HttpTransaction tx = new()
        {
            Method = "GET",
            Url = "https://example.com/api",
            StatusCode = 200,
            StatusReason = "OK",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddMilliseconds(50),
        };

        Assert.Contains("[proxy: (direct)]", tx.Summary, StringComparison.Ordinal);
    }
}
