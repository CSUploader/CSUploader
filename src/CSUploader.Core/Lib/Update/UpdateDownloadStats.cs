// <copyright file="UpdateDownloadStats.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib.Update;

/// <summary>
/// Turns the updater's percentage-only progress into a byte count, a download rate and a time
/// remaining.
/// <para>
/// Velopack reports an <c>int</c> from 0 to 100 and nothing else — no byte counts, no totals — so
/// every figure here is derived. Bytes come from the percentage against the size the release
/// advertised, which means they advance in whole-percent steps: on a 70 MB update that is a 700 kB
/// jump per tick, and the rate has to be measured across those steps rather than continuously.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Time remaining is computed from the PERCENTAGE rate, not the byte rate.</b> Not because the
/// arithmetic differs when the size is known — it does not, the total cancels out of both, so a
/// wrong size leaves the estimate correct either way. It is because the size can be missing
/// entirely, and a byte rate derived from a zero total is zero, which would silently drop the
/// estimate exactly when the percentage is the only thing left to work from.
/// </para>
/// <para>
/// Not thread-safe, and deliberately so: it is fed from the progress callback after that has been
/// marshalled to the UI thread, the same as every other progress surface in the app.
/// </para>
/// </remarks>
public sealed class UpdateDownloadStats
{
    /// <summary>
    /// How many per-step rates are averaged. Velopack emits at most 101 ticks for a whole download,
    /// so a long window would spend most of a short update still filling up; eight is enough to
    /// stop the readout flickering between adjacent percent steps without lagging a real change.
    /// </summary>
    private const int SampleWindow = 8;

    private readonly TimeProvider _clock;
    private readonly SpeedSampleBuffer _byteRates = new(SampleWindow);
    private readonly SpeedSampleBuffer _percentRates = new(SampleWindow);

    private long _totalBytes;
    private int _lastPercent = -1;
    private long _lastTimestamp;

    /// <param name="totalBytes">
    /// The expected download size, or 0 if it is unknown. See <see cref="UpdateDownloadProgress"/>
    /// for why this is only ever an estimate.
    /// </param>
    /// <param name="clock">Injected so the tests can advance time without spending it.</param>
    public UpdateDownloadStats(long totalBytes, TimeProvider? clock = null)
    {
        _totalBytes = Math.Max(0, totalBytes);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Folds one reported percentage into the running figures.</summary>
    public UpdateDownloadProgress Report(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        long now = _clock.GetTimestamp();

        if (percent < _lastPercent)
        {
            // The updater went BACKWARDS. That is what a delta download failing and restarting as
            // the full package looks like, so the rates measured against the old package are now
            // measuring the wrong thing, and the size estimate is wrong too. Start over rather than
            // average across the seam - and drop the total, because a fallback is precisely the case
            // where the advertised delta size no longer describes what is being fetched.
            Restart();
        }

        if (_lastPercent < 0)
        {
            // The first tick establishes the baseline. There is no interval to measure yet, and
            // measuring from construction would charge the download for whatever the caller did
            // before starting it.
            _lastPercent = percent;
            _lastTimestamp = now;
            return Snapshot(percent);
        }

        if (percent == _lastPercent)
        {
            // Velopack repeats values. Folding a zero-progress interval in would drag the average
            // toward zero for a download that is moving perfectly well.
            return Snapshot(percent);
        }

        double seconds = _clock.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        if (seconds > 0)
        {
            int steps = percent - _lastPercent;
            _percentRates.Add(steps / seconds);

            if (_totalBytes > 0)
            {
                _byteRates.Add(steps / 100.0 * _totalBytes / seconds);
            }
        }

        _lastPercent = percent;
        _lastTimestamp = now;
        return Snapshot(percent);
    }

    private void Restart()
    {
        _byteRates.Clear();
        _percentRates.Clear();
        _totalBytes = 0;
        _lastPercent = -1;
    }

    private UpdateDownloadProgress Snapshot(int percent)
    {
        long received = _totalBytes > 0 ? (long)(percent / 100.0 * _totalBytes) : 0;
        long rate = _totalBytes > 0 ? (long)Mean(_byteRates) : 0;

        double percentPerSecond = Mean(_percentRates);
        TimeSpan? remaining = percentPerSecond > 0 && percent < 100
            ? TimeSpan.FromSeconds((100 - percent) / percentPerSecond)
            : null;

        return new UpdateDownloadProgress(percent, received, _totalBytes, rate, remaining);
    }

    private static double Mean(SpeedSampleBuffer samples)
    {
        double[] values = samples.Snapshot();
        if (values.Length == 0)
        {
            return 0;
        }

        double total = 0;
        foreach (double value in values)
        {
            total += value;
        }

        return total / values.Length;
    }
}
