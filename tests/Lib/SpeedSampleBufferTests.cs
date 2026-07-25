// <copyright file="SpeedSampleBufferTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;

namespace CSUploader.Tests.Lib;

public sealed class SpeedSampleBufferTests
{
    [Fact]
    public void Snapshot_BeforeFull_ReturnsWhatWasAdded_OldestFirst()
    {
        SpeedSampleBuffer buffer = new(capacity: 5);
        buffer.Add(10);
        buffer.Add(20);
        buffer.Add(30);

        Assert.Equal(3, buffer.Count);
        Assert.Equal([10d, 20d, 30d], buffer.Snapshot());
    }

    [Fact]
    public void Add_BeyondCapacity_EvictsOldest_KeepsLastN_OldestFirst()
    {
        SpeedSampleBuffer buffer = new(capacity: 3);
        for (int i = 1; i <= 6; i++)
        {
            buffer.Add(i * 10); // 10,20,30,40,50,60
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal(3, buffer.Capacity);
        Assert.Equal([40d, 50d, 60d], buffer.Snapshot()); // last 3, oldest→newest
    }

    [Fact]
    public void Peak_IsMaxRetainedSample_NotEvicted ()
    {
        SpeedSampleBuffer buffer = new(capacity: 3);
        buffer.Add(100); // will be evicted
        buffer.Add(5);
        buffer.Add(9);
        buffer.Add(7); // evicts the 100

        Assert.Equal(9d, buffer.Peak()); // 100 fell out of the window
    }

    [Fact]
    public void Snapshot_Empty_IsEmpty_AndPeakZero()
    {
        SpeedSampleBuffer buffer = new(capacity: 4);
        Assert.Empty(buffer.Snapshot());
        Assert.Equal(0d, buffer.Peak());
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SpeedSampleBuffer(0));
}
