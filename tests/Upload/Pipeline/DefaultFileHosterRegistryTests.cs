// <copyright file="DefaultFileHosterRegistryTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload.Pipeline;

public class DefaultFileHosterRegistryTests
{
    [Fact]
    public void Find_ReturnsRegisteredPipelineByName()
    {
        Mock<IFileHosterPipeline> p = new();
        p.SetupGet(x => x.Name).Returns("Rapidgator");
        DefaultFileHosterRegistry registry = new([p.Object]);

        IFileHosterPipeline? found = registry.Find("Rapidgator");

        Assert.NotNull(found);
        Assert.Same(p.Object, found);
    }

    [Fact]
    public void Find_ReturnsNullWhenUnknown()
    {
        DefaultFileHosterRegistry registry = new([]);

        Assert.Null(registry.Find("DoesNotExist"));
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        Mock<IFileHosterPipeline> p = new();
        p.SetupGet(x => x.Name).Returns("Rapidgator");
        DefaultFileHosterRegistry registry = new([p.Object]);

        Assert.NotNull(registry.Find("rapidgator"));
        Assert.NotNull(registry.Find("RAPIDGATOR"));
    }
}
