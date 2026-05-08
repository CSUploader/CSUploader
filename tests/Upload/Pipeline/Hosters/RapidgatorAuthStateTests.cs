// <copyright file="RapidgatorAuthStateTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload.Pipeline.Hosters;

namespace CSUploader.Tests.Upload.Pipeline.Hosters;

public class RapidgatorAuthStateTests
{
    [Fact]
    public void Authenticated_HoldsTokenAndUserInfo()
    {
        RapidgatorAuthState state = new(Token: "tok", PrimaryFolderId: 42);

        Assert.Equal("tok", state.Token);
        Assert.Equal(42, state.PrimaryFolderId);
    }
}
