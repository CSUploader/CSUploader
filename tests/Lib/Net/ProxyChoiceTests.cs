// <copyright file="ProxyChoiceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net;

namespace CSUploader.Tests.Lib.Net;

public class ProxyChoiceTests
{
    [Fact]
    public void Direct_HasZeroId_AndNullWebProxy()
    {
        ProxyChoice direct = ProxyChoice.Direct;

        Assert.Equal(0, direct.Id);
        Assert.Null(direct.WebProxy);
        Assert.Equal("(direct)", direct.Description);
    }

    [Fact]
    public void Via_PreservesIdAndDescription()
    {
        ProxyChoice via = new(42, null, "http://example:8080");

        Assert.Equal(42, via.Id);
        Assert.Equal("http://example:8080", via.Description);
    }
}
