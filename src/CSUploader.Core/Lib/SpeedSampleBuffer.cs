// <copyright file="SpeedSampleBuffer.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib;

/// <summary>
/// Fixed-capacity ring of the most recent speed samples (bytes/sec) that backs the uploads toolbar's speed
/// sparkline. As new samples arrive the oldest fall off; <see cref="Snapshot"/> returns the retained samples
/// oldest→newest so the graph draws left (older) to right (newest). Not thread-safe — only touched on the UI
/// thread's refresh tick.
/// </summary>
public sealed class SpeedSampleBuffer
{
    private readonly double[] _ring;
    private int _count;
    private int _next; // index of the next slot to write

    public SpeedSampleBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _ring = new double[capacity];
    }

    /// <summary>Maximum samples retained (the sparkline's time window ÷ the sampling interval).</summary>
    public int Capacity => _ring.Length;

    /// <summary>Samples currently retained (grows to <see cref="Capacity"/>, then stays there).</summary>
    public int Count => _count;

    /// <summary>Records the newest sample, evicting the oldest once full.</summary>
    public void Add(double sample)
    {
        _ring[_next] = sample;
        _next = (_next + 1) % _ring.Length;
        if (_count < _ring.Length)
        {
            _count++;
        }
    }

    /// <summary>
    /// Drops every retained sample, as if newly constructed. For a series that has restarted and
    /// whose earlier samples now describe something else — averaging across that seam would report a
    /// rate that was never true of either side.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_ring);
        _count = 0;
        _next = 0;
    }

    /// <summary>A fresh array of the retained samples, oldest first (length == <see cref="Count"/>).</summary>
    public double[] Snapshot()
    {
        double[] result = new double[_count];
        int start = (_next - _count + _ring.Length) % _ring.Length; // oldest retained index
        for (int i = 0; i < _count; i++)
        {
            result[i] = _ring[(start + i) % _ring.Length];
        }

        return result;
    }

    /// <summary>The largest retained sample (0 when empty) — the sparkline's vertical scale.</summary>
    public double Peak()
    {
        double max = 0;
        for (int i = 0; i < _count; i++)
        {
            if (_ring[i] > max)
            {
                max = _ring[i];
            }
        }

        return max;
    }
}
