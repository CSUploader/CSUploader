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
/// every figure here is derived. Bytes come from <see cref="UpdateDownloadPlan"/>, which knows how
/// that percentage maps onto the packages being fetched; they advance in whole-percent steps, so on
/// a 70 MB update that is a 700 kB jump per tick and the rate has to be measured across those steps
/// rather than continuously.
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
    /// How many CHANGED-percentage samples the rate is measured across. Velopack emits at most a
    /// hundred of those for a whole download — fewer on the full path, which rounds to even
    /// percentages — so a long window would spend most of a short update still filling up; eight is
    /// enough to stop the readout flickering between adjacent steps without lagging a real change.
    /// </summary>
    private const int SampleWindow = 8;

    private readonly TimeProvider _clock;
    private readonly SpeedSampleBuffer _steps = new(SampleWindow);
    private readonly SpeedSampleBuffer _bytes = new(SampleWindow);
    private readonly SpeedSampleBuffer _seconds = new(SampleWindow);

    private UpdateDownloadPlan _plan;
    private int _lastPercent = -1;
    private long _lastTimestamp;

    /// <param name="plan">
    /// What the download will fetch, and how its percentage maps onto bytes. Pass
    /// <see cref="UpdateDownloadPlan.Unknown"/> when the size is not known — the countdown still
    /// works, only the byte figures go away.
    /// </param>
    /// <param name="clock">Injected so the tests can advance time without spending it.</param>
    public UpdateDownloadStats(UpdateDownloadPlan? plan = null, TimeProvider? clock = null)
    {
        _plan = plan ?? UpdateDownloadPlan.Unknown;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Folds one reported percentage into the running figures.</summary>
    public UpdateDownloadProgress Report(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        long now = _clock.GetTimestamp();

        if (percent < _lastPercent)
        {
            // The updater went BACKWARDS. Rates measured against the old package are now measuring
            // the wrong thing, and so is the plan - a fallback is precisely the case where what was
            // planned stops describing what is being fetched. Start over rather than average across
            // the seam.
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
            // Velopack repeats values. Folding a zero-progress interval in would drag the rate
            // toward zero for a download that is moving perfectly well - and the timestamp is left
            // ALONE, so the next real change is measured across the whole interval it took rather
            // than only since the last duplicate.
            return Snapshot(percent);
        }

        double seconds = _clock.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        if (seconds > 0)
        {
            _steps.Add(percent - _lastPercent);
            _bytes.Add(Math.Max(0, _plan.BytesAt(percent) - _plan.BytesAt(_lastPercent)));
            _seconds.Add(seconds);
        }

        _lastPercent = percent;
        _lastTimestamp = now;
        return Snapshot(percent);
    }

    private void Restart()
    {
        _steps.Clear();
        _bytes.Clear();
        _seconds.Clear();
        _plan = UpdateDownloadPlan.Unknown;
        _lastPercent = -1;
    }

    private UpdateDownloadProgress Snapshot(int percent)
    {
        long received = _plan.BytesAt(percent);

        // Totals over the window, not a mean of per-interval rates. The samples are spaced by
        // PROGRESS, not by time, so averaging their rates weights a 0.1 s step the same as a 10 s
        // one: seven fast percents and one slow one would report roughly the fast rate while the
        // window as a whole crawled. Dividing the sums is the window's actual throughput.
        double seconds = Total(_seconds);
        long rate = seconds > 0 && _plan.IsKnown ? (long)Math.Min(long.MaxValue, Total(_bytes) / seconds) : 0;

        double percentPerSecond = seconds > 0 ? Total(_steps) / seconds : 0;
        TimeSpan? remaining = percentPerSecond > 0 && percent < 100
            ? TimeSpan.FromSeconds(Math.Min((100 - percent) / percentPerSecond, MaxRemainingSeconds))
            : null;

        return new UpdateDownloadProgress(percent, received, _plan.TotalBytes, rate, remaining);
    }

    /// <summary>
    /// A day. Past this the figure is noise rather than information — a single sample taken across
    /// a laptop's sleep can imply centuries, and <see cref="TimeSpan.FromSeconds"/> throws outright
    /// past its own range.
    /// </summary>
    private const double MaxRemainingSeconds = 24 * 60 * 60;

    private static double Total(SpeedSampleBuffer samples)
    {
        double total = 0;
        foreach (double value in samples.Snapshot())
        {
            total += value;
        }

        return total;
    }
}
