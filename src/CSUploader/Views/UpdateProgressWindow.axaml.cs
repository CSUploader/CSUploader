// <copyright file="UpdateProgressWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Controls;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Update;

namespace CSUploader.Views;

/// <summary>
/// Non-modal update-download progress window (port of the WPF <c>UpdateProgressWindow</c>). Owned and
/// driven by <see cref="Services.AvaloniaUpdateProgressSink"/>; <see cref="SetStatus"/> /
/// <see cref="SetProgress"/> are identical to the WPF port.
/// </summary>
public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string status) => StatusText.Text = status;

    public void SetProgress(UpdateDownloadProgress progress)
    {
        Progress.Value = progress.Percent;
        PercentText.Text = progress.Percent.ToString(CultureInfo.InvariantCulture) + "%";
        BytesText.Text = FormatBytes(progress);
        StatsText.Text = FormatStats(progress);
    }

    /// <summary>
    /// "24.8 MiB of 71.3 MiB", or nothing at all when the size is unknown. Binary units, explicitly:
    /// this is a measured quantity, and <c>FromBytesPreferRoundUnit</c> is for quoting a cap a host
    /// stated in decimal, not for a figure that changes as it is watched.
    /// </summary>
    private static string FormatBytes(UpdateDownloadProgress progress) => progress.HasBytes
        ? string.Format(
            CultureInfo.CurrentCulture,
            Localizer.Instance["UpdateProgress_BytesFormat"],
            ByteUnit.FromBytes(progress.BytesReceived, ByteBase.Binary).ToFriendlyString(),
            ByteUnit.FromBytes(progress.TotalBytes, ByteBase.Binary).ToFriendlyString())
        : string.Empty;

    /// <summary>
    /// "3.1 MiB/s · 15s left" — or whichever half exists. The rate needs a known size and the time
    /// does not, so a download of unknown size still shows a countdown, and the first moments of any
    /// download show neither rather than a zero.
    /// </summary>
    private static string FormatStats(UpdateDownloadProgress progress)
    {
        string? rate = progress.HasRate
            ? ByteUnit.FromBytes(progress.BytesPerSecond, ByteBase.Binary).ToFriendlyString()
            : null;
        string? left = progress.Remaining is { } remaining ? FormatDuration(remaining) : null;

        return (rate, left) switch
        {
            (not null, not null) => Format("UpdateProgress_SpeedAndLeft_Format", rate, left),
            (not null, null) => Format("UpdateProgress_SpeedOnly_Format", rate, string.Empty),
            (null, not null) => Format("UpdateProgress_LeftOnly_Format", string.Empty, left),
            _ => string.Empty,
        };

        static string Format(string key, string rate, string left) =>
            string.Format(CultureInfo.CurrentCulture, Localizer.Instance[key], rate, left);
    }

    /// <summary>
    /// Compact, and the same shape the uploads toolbar uses: <c>5h:03m:20s</c> / <c>03m:20s</c> /
    /// <c>20s</c>. Hours are the TOTAL count, so a long download does not wrap at a day.
    /// </summary>
    private static string FormatDuration(TimeSpan span)
    {
        int totalHours = (int)span.TotalHours;
        return totalHours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{totalHours}h:{span.Minutes:00}m:{span.Seconds:00}s")
            : span.Minutes > 0
                ? span.ToString(@"mm\m\:ss\s", CultureInfo.InvariantCulture)
                : span.ToString(@"ss\s", CultureInfo.InvariantCulture);
    }
}
