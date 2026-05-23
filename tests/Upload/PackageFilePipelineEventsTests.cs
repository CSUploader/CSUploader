// <copyright file="PackageFilePipelineEventsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using Moq;

namespace CSUploader.Tests.Upload;

public class PackageFilePipelineEventsTests
{
    [Fact]
    public void ApplyEvent_TransferProgress_UpdatesProgressFields()
    {
        PackageFile file = MakeFile(out _);

        file.ApplyEvent(new TransferProgress(BytesUploaded: 50, TotalBytes: 100, SpeedBytesPerSec: 1024));

        Assert.Equal(50, file.BytesLoaded);
        Assert.Equal(50, file.BytesRemaining);
        Assert.Equal(50.0, file.Progress);
    }

    [Fact]
    public void ApplyEvent_TransferCompleted_SetsFinishedAndUrl()
    {
        PackageFile file = MakeFile(out _);

        file.ApplyEvent(new TransferCompleted("https://x/y"));

        Assert.True(file.IsUploadFinished);
        Assert.Equal("https://x/y", file.FileUrl);
        Assert.Equal(100.0, file.Progress);
    }

    [Fact]
    public void ApplyEvent_AttemptFailed_SetsError()
    {
        PackageFile file = MakeFile(out _);

        file.ApplyEvent(new AttemptFailed("network down", null));

        Assert.Equal("network down", file.Error);
    }

    [Fact]
    public void ApplyEvent_AttemptFailed_StoresErrorVerbatim()
    {
        // Multi-line error payloads (e.g. BRupload's HTML 500 page) are kept verbatim
        // on the model so copy-to-clipboard surfaces the original message — the
        // Uploads grid uses SingleLineConverter to keep the display row to one line.
        PackageFile file = MakeFile(out _);

        const string raw = "upload.cgi: file_status=failed\r\n<HEAD>\n<TITLE>500</TITLE>\n</HEAD>";
        file.ApplyEvent(new AttemptFailed(raw, null));

        Assert.Equal(raw, file.Error);
    }

    [Fact]
    public void ApplyEvent_TransferProgress_ComputesTimeRemainingFromSpeed()
    {
        PackageFile file = MakeFile(out _);
        file.ApplyEvent(new TransferStarted(TotalBytes: 1000));

        // 50/1000 done at 100 B/s → 950 bytes left → 9.5 seconds remaining.
        file.ApplyEvent(new TransferProgress(BytesUploaded: 50, TotalBytes: 1000, SpeedBytesPerSec: 100.0));

        Assert.NotNull(file.TimeRemaining);
        Assert.Equal(9.5, file.TimeRemaining!.Value.TotalSeconds, precision: 1);
    }

    [Fact]
    public void ApplyEvent_TransferProgress_LeavesTimeRemainingNullWhenSpeedIsZero()
    {
        PackageFile file = MakeFile(out _);
        file.ApplyEvent(new TransferStarted(TotalBytes: 1000));

        file.ApplyEvent(new TransferProgress(BytesUploaded: 50, TotalBytes: 1000, SpeedBytesPerSec: 0.0));

        Assert.Null(file.TimeRemaining);
    }

    [Fact]
    public void ApplyEvent_TransferProgress_UpdatesDurationFromStartedDate()
    {
        PackageFile file = MakeFile(out _);
        file.ApplyEvent(new TransferStarted(TotalBytes: 1000));

        // The pipeline runs progress events ~immediately after Started; Duration must be
        // a small but non-null TimeSpan (was permanently null before the fix).
        file.ApplyEvent(new TransferProgress(BytesUploaded: 50, TotalBytes: 1000, SpeedBytesPerSec: 100.0));

        Assert.NotNull(file.Duration);
        Assert.True(file.Duration!.Value.TotalSeconds < 5, "Duration drifted unexpectedly large for an immediate progress event");
    }

    [Fact]
    public void ApplyEvent_TransferCompleted_ClearsTimeRemainingAndFinalizesDuration()
    {
        PackageFile file = MakeFile(out _);
        file.ApplyEvent(new TransferStarted(TotalBytes: 1000));
        file.ApplyEvent(new TransferProgress(BytesUploaded: 50, TotalBytes: 1000, SpeedBytesPerSec: 100.0));

        file.ApplyEvent(new TransferCompleted("https://x/y"));

        Assert.Null(file.TimeRemaining);
        Assert.NotNull(file.Duration);
    }

    [Fact]
    public void ApplyEvent_TransferStarted_ClearsStaleStateFromPriorAttempt()
    {
        PackageFile file = MakeFile(out _);

        // Simulate stale state from a prior attempt that failed mid-upload.
        file.Error = "prior error";
        file.Speed = 12345L;
        file.Progress = 75.0;
        file.FinishedDate = DateTime.Now.AddMinutes(-5);

        file.ApplyEvent(new TransferStarted(TotalBytes: 1000));

        Assert.Null(file.Error);
        Assert.Null(file.Speed);
        Assert.Equal(0.0, file.Progress);
        Assert.Null(file.FinishedDate);
        Assert.Equal(1000, file.BytesRemaining);
    }

    private static PackageFile MakeFile(out FileHosterClient client)
    {
        // Use any tempfile path that exists for the FileInfo construction
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "x");
        Package pkg = new(new PackageOptions { Title = "test", SelectedFiles = [path], Logger = Mock.Of<IAppLogger>() });
        client = new FileHosterClient("Stub", Protocol.Http);
        PackageFile file = new(pkg, path, client, new FileHosterLoginDto());
        return file;
    }
}
