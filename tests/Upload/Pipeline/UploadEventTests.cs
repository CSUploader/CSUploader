// <copyright file="UploadEventTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib.Net;
using CSUploader.Upload.Pipeline;

namespace CSUploader.Tests.Upload.Pipeline;

public class UploadEventTests
{
    [Fact]
    public void AttemptCompleted_CarriesProxyIdAndOutcome()
    {
        AttemptCompleted ev = new(Success: true, ProxyId: 7, FileUrl: "https://x/y");

        Assert.True(ev.Success);
        Assert.Equal(7, ev.ProxyId);
        Assert.Equal("https://x/y", ev.FileUrl);
    }

    [Fact]
    public void TransferProgress_ComputesPercentage()
    {
        TransferProgress ev = new(BytesUploaded: 25, TotalBytes: 100, SpeedBytesPerSec: 1024);

        Assert.Equal(25.0, ev.PercentComplete);
    }

    [Fact]
    public void TransferProgress_HandlesZeroTotal()
    {
        TransferProgress ev = new(BytesUploaded: 0, TotalBytes: 0, SpeedBytesPerSec: 0);

        Assert.Equal(0.0, ev.PercentComplete);
    }

    [Fact]
    public void ProxyPicked_RecordsTheChoice()
    {
        ProxyPicked ev = new(ProxyChoice.Direct);

        Assert.Same(ProxyChoice.Direct, ev.Proxy);
    }
}
