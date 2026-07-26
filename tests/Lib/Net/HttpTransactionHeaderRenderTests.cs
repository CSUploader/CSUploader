// <copyright file="HttpTransactionHeaderRenderTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net.Http;
using Xunit;

namespace CSUploader.Tests.Lib.Net;

/// <summary>
/// The Logs tab's header rendering. Regression: .NET stores User-Agent pre-parsed into its product
/// tokens, and printing one value per line made a perfectly normal request look like it sent seven
/// User-Agent headers — which cost real time during a Cloudflare-challenge investigation.
/// </summary>
public class HttpTransactionHeaderRenderTests
{
    [Fact]
    public void RequestHeadersText_RejoinsUserAgentProductTokensOnOneLine()
    {
        HttpTransaction tx = new()
        {
            Method = "GET",
            Url = "https://example.test/",
            RequestHeaders = new()
            {
                // Exactly how .NET hands back a browser UA added via TryAddWithoutValidation.
                ["User-Agent"] = ["Mozilla/5.0", "(Windows NT 10.0; Win64; x64)", "Chrome/148.0.0.0"],
                ["Accept"] = ["text/html", "application/xhtml+xml"],
            },
        };

        string text = tx.RequestHeadersText;

        // One line per header, product tokens space-joined (the wire form).
        Assert.Contains("User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/148.0.0.0", text, StringComparison.Ordinal);
        // Ordinary multi-valued headers keep the standard comma join.
        Assert.Contains("Accept: text/html, application/xhtml+xml", text, StringComparison.Ordinal);
        // And there is exactly ONE User-Agent line.
        Assert.Equal(1, text.Split('\n').Count(l => l.StartsWith("User-Agent:", StringComparison.Ordinal)));
    }

    [Fact]
    public void ResponseHeadersText_JoinsServerProductTokensWithSpace()
    {
        HttpTransaction tx = new()
        {
            StatusCode = 200,
            StatusReason = "OK",
            ResponseHeaders = new() { ["Server"] = ["nginx/1.2.3", "(Ubuntu)"], ["Set-Cookie"] = ["a=1", "b=2"] },
        };

        string text = tx.ResponseHeadersText;

        Assert.Contains("Server: nginx/1.2.3 (Ubuntu)", text, StringComparison.Ordinal);
        Assert.Contains("Set-Cookie: a=1, b=2", text, StringComparison.Ordinal);
    }
}
