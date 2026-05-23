// <copyright file="AlfafilePipelineCheckAccountTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Lib;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline.Hosters;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

public class AlfafilePipelineCheckAccountTests
{
    [Fact]
    public async Task CheckAccountAsync_PremiumWithExpiry_ReturnsPremiumAndDate()
    {
        // 2030-06-01 00:00:00 UTC
        long expiryUnix = new DateTimeOffset(2030, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        string body = "{\"response\":{\"token\":\"TOK\",\"user\":{\"email\":\"u@example.com\",\"is_premium\":true,\"premium_end_time\":"
            + expiryUnix
            + "}},\"status\":200,\"details\":null}";
        Queue<string> responses = new(new[] { body });
        AlfafilePipeline pipeline = new(url => responses.Dequeue());

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "secret", MakeHandler(), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Premium, result.AccountType);
        Assert.Equal(new DateTime(2030, 6, 1, 0, 0, 0, DateTimeKind.Utc), result.PremiumExpiry);
    }

    [Fact]
    public async Task CheckAccountAsync_FreeUser_ReturnsFree()
    {
        Queue<string> responses = new(new[]
        {
            """{"response":{"token":"TOK","user":{"email":"u@example.com","is_premium":false,"premium_end_time":null}},"status":200,"details":null}""",
        });
        AlfafilePipeline pipeline = new(url => responses.Dequeue());

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "secret", MakeHandler(), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(AccountType.Free, result.AccountType);
        Assert.Null(result.PremiumExpiry);
    }

    [Fact]
    public async Task CheckAccountAsync_WrongCredentials_ReturnsInvalid()
    {
        Queue<string> responses = new(new[]
        {
            """{"response":null,"status":401,"details":"Unauthorized. Wrong login or password."}""",
        });
        AlfafilePipeline pipeline = new(url => responses.Dequeue());

        AccountCheckResult result = await pipeline.CheckAccountAsync("u@example.com", "wrong", MakeHandler(), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("Wrong login", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpHandler MakeHandler()
        => new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
}
