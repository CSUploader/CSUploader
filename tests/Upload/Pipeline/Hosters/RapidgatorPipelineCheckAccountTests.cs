// <copyright file="RapidgatorPipelineCheckAccountTests.cs" company="CSUploader">
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

public class RapidgatorPipelineCheckAccountTests
{
    [Fact]
    public async Task CheckAccountAsync_PremiumWithExpiry_ReturnsPremiumAndDate()
    {
        // 2030-06-01 00:00:00 UTC
        long expiryUnix = new DateTimeOffset(2030, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        string body = "{\"response\":{\"token\":\"TOK\",\"user\":{\"email\":\"u@example.com\",\"is_premium\":true,\"premium_end_time\":"
            + expiryUnix
            + ",\"folder_id\":42}},\"status\":200,\"details\":null}";
        Queue<string> responses = new(new[] { body });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "secret", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Premium, result.AccountType);
        Assert.NotNull(result.PremiumExpiry);
        Assert.Equal(new DateTime(2030, 6, 1, 0, 0, 0, DateTimeKind.Utc), result.PremiumExpiry!.Value);
        Assert.Contains("2030-06-01", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAccountAsync_FreeUser_ReturnsFreeWithNoExpiry()
    {
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK","user":{"email":"u@example.com","is_premium":false,"premium_end_time":null,"folder_id":1}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "secret", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Free, result.AccountType);
        Assert.Null(result.PremiumExpiry);
    }

    [Fact]
    public async Task CheckAccountAsync_WrongPassword_ReturnsInvalidWithServerMessage()
    {
        Queue<string> responses = new(new[]
        {
            """{"response":null,"status":401,"details":"Login or password is incorrect"}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "wrong", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("incorrect", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAccountAsync_UnexpectedBody_ReturnsInvalid()
    {
        Queue<string> responses = new(new[] { "<html>nope</html>" });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "secret", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task CheckAccountAsync_PopulatesStorageFromStorageBlock_UsedIsTotalMinusLeft()
    {
        // Exactly the documented shape: total is a JSON STRING, left a NUMBER (both must parse).
        // total = 4 TiB; used = total - left = 4398046511104 - 4398035213138 = 11297966.
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK","user":{"email":"u@example.com","is_premium":false,"premium_end_time":null,"folder_id":1,"storage":{"total":"4398046511104","left":4398035213138}}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "secret", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(4398046511104, result.StorageQuotaBytes);
        Assert.Equal(11297966, result.StorageUsedBytes);
    }

    [Theory]
    // A malformed storage scalar must NOT sink the login envelope: storage rides in the same
    // /user/login response the upload path parses, so a bad stats field has to degrade to blank
    // storage on a still-VALID account rather than throw and fail the login (and the upload).
    [InlineData("\"\"")]      // empty string
    [InlineData("\"abc\"")]   // non-numeric string
    [InlineData("1000.5")]    // float
    [InlineData("true")]      // bool
    public async Task CheckAccountAsync_MalformedStorageScalar_StaysValidWithNullStorage(string totalJson)
    {
        Queue<string> responses = new(new[]
        {
            "{\"response\":{\"token\":\"TOK\",\"user\":{\"email\":\"u@example.com\",\"is_premium\":false,\"premium_end_time\":null,\"folder_id\":1,\"storage\":{\"total\":"
            + totalJson + ",\"left\":400}}},\"status\":200,\"details\":null}",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "secret", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Null(result.StorageUsedBytes);
        Assert.Null(result.StorageQuotaBytes);
    }

    [Fact]
    public async Task CheckAccountAsync_NoStorageBlock_LeavesStorageNull()
    {
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK","user":{"email":"u@example.com","is_premium":false,"premium_end_time":null,"folder_id":1}},"status":200,"details":null}""",
        });
        RapidgatorPipeline pipeline = new(url => responses.Dequeue());

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "secret", apiKey: null, MakeHandler(), ProxyChoice.Direct, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Null(result.StorageUsedBytes);
        Assert.Null(result.StorageQuotaBytes);
    }

    private static HttpHandler MakeHandler()
        => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
}
