// <copyright file="AttemptContextTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline;

public class AttemptContextTests
{
    [Fact]
    public void With_PreservesUntouchedFields()
    {
        FileHosterLoginDto creds = new() { Id = 5, FileHosterName = "X", Username = "u", Password = "p" };
        HttpHandler handler = new(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled);
        AttemptContext ctx = new()
        {
            AttemptId = Guid.NewGuid(),
            FilePath = "/tmp/x.zip",
            FileName = "x.zip",
            FileSize = 100,
            FileHash = null,
            HosterName = "Rapidgator",
            Credentials = creds,
            Proxy = ProxyChoice.Direct,
            Handler = handler,
            Logger = Mock.Of<IAppLogger>(),
            SpeedLimitProvider = () => null,
            Cancellation = default,
        };

        AttemptContext copy = ctx with { FileHash = "abcd" };

        Assert.Equal("abcd", copy.FileHash);
        Assert.Same(creds, copy.Credentials);
        Assert.Same(handler, copy.Handler); // non-nullable by record signature
    }
}
