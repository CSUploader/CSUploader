// <copyright file="HitFilePipelineRefreshTests.cs" company="CSUploader">
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

/// <summary>
/// Covers <see cref="HitFilePipeline.RefreshAccountAsync"/> — the C# re-read of storage usage with
/// the captured session cookies (the no-WebView "Check / Refresh") — and the size parser it sums with.
/// </summary>
public class HitFilePipelineRefreshTests
{
    private const string AppIdUrl = "https://app.hitfile.net/api/user/app/id";
    private const string FolderContentUrl = "https://app.hitfile.net/api/folder/content";
    private const string Cookie = "kohanasession7=abc; fd_session=xyz";

    [Theory]
    [InlineData("4,98 Mb", 5221908L)]   // comma decimal + binary Mb (4.98 * 1024^2, half-up rounded)
    [InlineData("1 Mb", 1048576L)]
    [InlineData("1.5 Kb", 1536L)]
    [InlineData("2 Gb", 2147483648L)]
    [InlineData("512 b", 512L)]
    [InlineData("0 b", 0L)]
    [InlineData("", 0L)]
    [InlineData(null, 0L)]
    [InlineData("garbage", 0L)]
    public void ParseHumanSize_BinaryUnitsAndCommaDecimal(string? input, long expected)
    {
        Assert.Equal(expected, HitFilePipeline.ParseHumanSize(input));
    }

    [Fact]
    public async Task RefreshAccountAsync_ValidSession_WalksFoldersAndSumsUsage()
    {
        // Root has a file + a (string-id) subfolder; the subfolder has another file. The walk must
        // recurse and sum BOTH (1 Mb + 2,50 Mb), forward the session cookie on every call, and send
        // app/id body-LESS.
        List<(string Url, string? Body, string? Cookie)> calls = [];
        HitFilePipeline pipeline = new(cookiePostOverride: (url, body, headers) =>
        {
            calls.Add((url, body, headers.TryGetValue("Cookie", out string? c) ? c : null));
            if (url == AppIdUrl)
            {
                return new HttpResponseSnapshot(200, """{"appId":"ABC123"}""", Array.Empty<string>());
            }

            string responseBody = FolderId(body) switch
            {
                "null" => """{"items":[{"type":"file","size":"1 Mb"},{"type":"folder","id":"42"}],"total":2}""",
                "\"42\"" => """{"items":[{"type":"file","size":"2,50 Mb"}],"total":1}""",
                _ => """{"items":[],"total":0}""",
            };
            return new HttpResponseSnapshot(200, responseBody, Array.Empty<string>());
        });

        AccountCheckResult result = await pipeline.RefreshAccountAsync("APPID", Cookie, Handler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("APPID", result.ApiKey);
        Assert.Equal((1L * 1024 * 1024) + 2621440L, result.StorageUsedBytes); // 1 Mb + 2.50 Mb
        Assert.Null(result.StorageQuotaBytes); // unlimited
        Assert.Contains("refreshed", result.Message, StringComparison.OrdinalIgnoreCase);

        Assert.All(calls, c => Assert.Equal(Cookie, c.Cookie));               // cookie forwarded everywhere
        Assert.Null(calls.Single(c => c.Url == AppIdUrl).Body);              // app/id is body-less
        Assert.Contains(calls, c => c.Url == FolderContentUrl && FolderId(c.Body) == "\"42\""); // recursed
    }

    [Fact]
    public async Task RefreshAccountAsync_NumericSubfolderId_SendsUnquotedFolderId()
    {
        // Regression: a numeric folder id must go on the wire as folder_id:42 (a JSON number), the
        // way the SPA sends it — NOT re-quoted as "42", which a strict-typed API would miss.
        List<string?> folderIds = [];
        HitFilePipeline pipeline = new(cookiePostOverride: (url, body, headers) =>
        {
            if (url == AppIdUrl)
            {
                return new HttpResponseSnapshot(200, """{"appId":"ABC123"}""", Array.Empty<string>());
            }

            folderIds.Add(FolderId(body));
            string responseBody = FolderId(body) == "null"
                ? """{"items":[{"type":"folder","id":42}],"total":1}"""
                : """{"items":[{"type":"file","size":"1 Mb"}],"total":1}""";
            return new HttpResponseSnapshot(200, responseBody, Array.Empty<string>());
        });

        AccountCheckResult result = await pipeline.RefreshAccountAsync("APPID", Cookie, Handler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.Equal(1048576L, result.StorageUsedBytes);   // the subfolder's file WAS counted
        Assert.Contains("42", folderIds);                  // unquoted numeric id, not "\"42\""
        Assert.DoesNotContain("\"42\"", folderIds);
    }

    [Fact]
    public async Task RefreshAccountAsync_ExpiredSession_KeepsAccountAndOmitsStorage()
    {
        // app/id returns appId:null for an unauthenticated session → we must NOT walk folders, must
        // keep the account valid, and must omit storage so the caller preserves the last figure.
        List<string> urls = [];
        HitFilePipeline pipeline = new(cookiePostOverride: (url, body, headers) =>
        {
            urls.Add(url);
            return new HttpResponseSnapshot(200, """{"appId":null}""", Array.Empty<string>());
        });

        AccountCheckResult result = await pipeline.RefreshAccountAsync("APPID", Cookie, Handler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);             // account kept
        Assert.Equal("APPID", result.ApiKey);
        Assert.Null(result.StorageUsedBytes);    // no clobber
        Assert.Null(result.StorageQuotaBytes);
        Assert.Contains("expired", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FolderContentUrl, urls); // never walked folders
    }

    [Fact]
    public async Task RefreshAccountAsync_AppIdCallFails_TreatedAsExpired()
    {
        // A non-2xx on the validity probe keeps the account and doesn't touch storage.
        HitFilePipeline pipeline = new(cookiePostOverride: (url, body, headers) =>
            new HttpResponseSnapshot(503, "<html>busy</html>", Array.Empty<string>()));

        AccountCheckResult result = await pipeline.RefreshAccountAsync("APPID", Cookie, Handler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Null(result.StorageUsedBytes);
    }

    [Fact]
    public async Task RefreshAccountAsync_AuthenticatedButRootReadFails_PreservesStorage()
    {
        // app/id authenticates, but the ROOT folder/content 5xxes → we must return "no figure"
        // (null storage) so the last-known value is preserved, NOT a spurious 0 that clobbers it.
        HitFilePipeline pipeline = new(cookiePostOverride: (url, body, headers) =>
            url == AppIdUrl
                ? new HttpResponseSnapshot(200, """{"appId":"ABC123"}""", Array.Empty<string>())
                : new HttpResponseSnapshot(503, "<html>busy</html>", Array.Empty<string>()));

        AccountCheckResult result = await pipeline.RefreshAccountAsync("APPID", Cookie, Handler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Null(result.StorageUsedBytes);    // failed root read => preserve, don't report 0
        Assert.Contains("expired", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAccountAsync_AuthenticatedAndRootEmpty_ReportsZero()
    {
        // A genuinely empty account (root reads OK, no files) correctly reports 0 — distinct from
        // the failed-read case above.
        HitFilePipeline pipeline = new(cookiePostOverride: (url, body, headers) =>
            url == AppIdUrl
                ? new HttpResponseSnapshot(200, """{"appId":"ABC123"}""", Array.Empty<string>())
                : new HttpResponseSnapshot(200, """{"items":[],"total":0}""", Array.Empty<string>()));

        AccountCheckResult result = await pipeline.RefreshAccountAsync("APPID", Cookie, Handler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(0L, result.StorageUsedBytes);
        Assert.Contains("refreshed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Extracts the folder_id token verbatim from a folder/content request body (e.g. "null", "42",
    // "\"abc\"") so tests can assert the exact JSON type sent.
    private static string? FolderId(string? body)
    {
        if (body is null)
        {
            return null;
        }

        const string Key = "\"folder_id\":";
        int start = body.IndexOf(Key, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += Key.Length;
        int end = body.IndexOf(',', start);
        return end < 0 ? body[start..] : body[start..end];
    }

    private static HttpHandler Handler() =>
        new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
}
