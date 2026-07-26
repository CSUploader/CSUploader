// <copyright file="PackageFileRequeueDisplayTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using Moq;
using Xunit;

namespace CSUploader.Tests.Upload;

/// <summary>
/// <see cref="PackageFile.ResetAttemptDisplay"/> — invoked by both re-queue transitions
/// (<c>PackageManager.ForceQueueIfStartable</c> and <c>UploadScheduler.RequeueStartableFiles</c>) so a
/// re-queued Failed/Cancelled row stops showing the dead attempt's Elapsed/Started/Finished/progress
/// while it waits. The regression: after a restart the loader rebuilds those from the persisted attempt,
/// and Start-all re-queued the row still wearing the old attempt's 3-hour Duration.
/// </summary>
public class PackageFileRequeueDisplayTests
{
    [Fact]
    public void ResetAttemptDisplay_ClearsEveryPerAttemptValue_AndRestoresBytesRemainingToSize()
    {
        FileHosterClient hoster = new("TestHost", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "TestHost", IsAnonymous = true };
        Package pkg = new(new PackageOptions
        {
            Title = "p",
            Logger = Mock.Of<IAppLogger>(),
            Settings = new AppSettings(),
            FileHosters = new() { { hoster, login } },
        });
        PackageFile file = new(pkg, @"C:\d\a.bin", hoster, login) { Size = 1000 };

        // The dead attempt's footprint, exactly as the loader restores it after a restart.
        file.State = FileState.Failed;
        file.StartedDate = DateTime.Now.AddHours(-4);
        file.FinishedDate = DateTime.Now.AddHours(-1);
        file.Duration = TimeSpan.FromHours(3);
        file.Speed = 12345;
        file.TimeRemaining = TimeSpan.FromMinutes(5);
        file.BytesLoaded = 400;
        file.Progress = 40.0;
        file.BytesRemaining = 600;

        file.ResetAttemptDisplay();

        // Reads like a queued file again — no trace of the dead attempt.
        Assert.Null(file.StartedDate);
        Assert.Null(file.FinishedDate);
        Assert.Null(file.Duration);
        Assert.Null(file.Speed);
        Assert.Null(file.TimeRemaining);
        Assert.Null(file.BytesLoaded);
        Assert.Null(file.Progress);
        Assert.Equal(1000, file.BytesRemaining); // back to the full size, like a fresh queue entry
        Assert.Equal(FileState.Failed, file.State); // state transitions stay the call sites' job
    }
}
