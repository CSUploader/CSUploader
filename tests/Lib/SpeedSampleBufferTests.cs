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

    /// <summary>
    /// <see cref="SpeedSampleBuffer.Clear"/> has to reset the COUNT, or every reader keeps seeing
    /// samples the caller believes it discarded.
    /// </summary>
    [Fact]
    public void Clear_LeavesTheBufferIndistinguishableFromANewOne()
    {
        SpeedSampleBuffer buffer = new(capacity: 4);
        buffer.Add(3);
        buffer.Add(9);

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Snapshot());
        Assert.Equal(0d, buffer.Peak());
    }

    /// <summary>
    /// ...and the WRITE POSITION too, which <see cref="SpeedSampleBuffer.Peak"/> is what exposes.
    /// <para>
    /// <c>Peak</c> scans the first <c>Count</c> slots rather than walking back from the cursor the
    /// way <c>Snapshot</c> does. They cover the same slots as long as
    /// <c>_count == Capacity || _next == _count</c> — either the buffer is full, where both read
    /// everything whatever the cursor is, or it is still filling and the cursor sits exactly at the
    /// count. A <c>Clear</c> that reset the count without rewinding the cursor CAN land between the
    /// two — not full, cursor past the count — which is whenever the cursor was not already at zero,
    /// as it is here. The next sample then falls outside the prefix
    /// <c>Peak</c> reads, so it answers from before the clear — here the discarded 3 rather than the
    /// 42 just added, while <c>Snapshot</c> correctly returns [42].
    /// </para>
    /// </summary>
    [Fact]
    public void Clear_RewindsTheWritePosition_SoPeakDoesNotReportAPreClearSample()
    {
        SpeedSampleBuffer buffer = new(capacity: 4);
        buffer.Add(3);
        buffer.Add(9);

        buffer.Clear();
        buffer.Add(42);

        Assert.Equal(1, buffer.Count);
        Assert.Equal([42d], buffer.Snapshot());
        Assert.Equal(42d, buffer.Peak());
    }

    /// <summary>Clearing a full buffer, then refilling it, behaves like a fresh one.</summary>
    [Fact]
    public void Clear_OnAFullBuffer_StartsTheWindowOver()
    {
        SpeedSampleBuffer buffer = new(capacity: 3);
        buffer.Add(100);
        buffer.Add(200);
        buffer.Add(300);

        buffer.Clear();
        buffer.Add(1);
        buffer.Add(2);

        Assert.Equal(2, buffer.Count);
        Assert.Equal([1d, 2d], buffer.Snapshot());
        Assert.Equal(2d, buffer.Peak());
    }

    [Fact]
    public void Clear_OnAnEmptyBuffer_IsHarmless()
    {
        SpeedSampleBuffer buffer = new(capacity: 2);

        buffer.Clear();
        buffer.Add(7);

        Assert.Equal([7d], buffer.Snapshot());
    }
}
