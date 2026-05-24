// <copyright file="ConverterTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows;
using CSUploader.Converters;

namespace CSUploader.Tests.Converters;

public class ByteUnitConverterTests
{
    private readonly ByteUnitConverter _converter = new();

    [Fact]
    public void Convert_LongBytes_ReturnsFriendlyString()
    {
        // 1 KiB = 1024 bytes in binary mode
        object result = _converter.Convert(1024L, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.IsType<string>(result);
        Assert.Contains("KiB", (string)result, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_ZeroBytes_ReturnsBString()
    {
        object result = _converter.Convert(0L, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.IsType<string>(result);
        Assert.Contains("B", (string)result, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_LargeBytes_ReturnsMiBOrHigher()
    {
        // 5 MiB = 5 * 1024 * 1024
        long fiveMiB = 5L * 1024 * 1024;
        object result = _converter.Convert(fiveMiB, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.IsType<string>(result);
        string text = (string)result;
        Assert.Contains("MiB", text, StringComparison.Ordinal);
        Assert.StartsWith("5", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_Null_ReturnsEmptyString()
    {
        object result = _converter.Convert(null!, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Convert_NonLongType_ReturnsEmptyString()
    {
        object result = _converter.Convert("not a long", typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => _converter.ConvertBack("1 KiB", typeof(long), null!, CultureInfo.InvariantCulture));
    }
}

public class TimeSpanFormatConverterTests
{
    private readonly TimeSpanFormatConverter _converter = new();

    [Fact]
    public void Convert_HoursMinutesSeconds_FormatsWithH()
    {
        var timeSpan = new TimeSpan(2, 30, 45);

        object result = _converter.Convert(timeSpan, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal("2h:30m:45s", result);
    }

    [Fact]
    public void Convert_MinutesAndSeconds_FormatsWithM()
    {
        var timeSpan = new TimeSpan(0, 15, 30);

        object result = _converter.Convert(timeSpan, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal("15m:30s", result);
    }

    [Fact]
    public void Convert_SecondsOnly_FormatsWithS()
    {
        var timeSpan = new TimeSpan(0, 0, 42);

        object result = _converter.Convert(timeSpan, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal("42s", result);
    }

    [Fact]
    public void Convert_ZeroTimeSpan_ReturnsZeroSeconds()
    {
        object result = _converter.Convert(TimeSpan.Zero, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal("00s", result);
    }

    [Fact]
    public void Convert_Null_ReturnsEmptyString()
    {
        object result = _converter.Convert(null!, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Convert_NonTimeSpanType_ReturnsEmptyString()
    {
        object result = _converter.Convert(12345, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => _converter.ConvertBack("15m:30s", typeof(TimeSpan), null!, CultureInfo.InvariantCulture));
    }
}

public class DateTimeFormatConverterTests
{
    private readonly DateTimeFormatConverter _converter = new();

    [Fact]
    public void Convert_DateTime_FormatsCorrectly()
    {
        var dateTime = new DateTime(2025, 3, 15, 14, 30, 45);

        object result = _converter.Convert(dateTime, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal("2025/03/15 14:30:45", result);
    }

    [Fact]
    public void Convert_MidnightDateTime_FormatsWithZeroes()
    {
        var dateTime = new DateTime(2024, 1, 1, 0, 0, 0);

        object result = _converter.Convert(dateTime, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal("2024/01/01 00:00:00", result);
    }

    [Fact]
    public void Convert_Null_ReturnsEmptyString()
    {
        object result = _converter.Convert(null!, typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Convert_NonDateTimeType_ReturnsEmptyString()
    {
        object result = _converter.Convert("not a datetime", typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => _converter.ConvertBack("2025/03/15 14:30:45", typeof(DateTime), null!, CultureInfo.InvariantCulture));
    }
}

public class BoolToVisibilityConverterTests
{
    private readonly BoolToVisibilityConverter _converter = new();

    [Fact]
    public void Convert_True_ReturnsVisible()
    {
        object result = _converter.Convert(true, typeof(Visibility), null!, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void Convert_False_ReturnsCollapsed()
    {
        object result = _converter.Convert(false, typeof(Visibility), null!, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void Convert_Null_ReturnsCollapsed()
    {
        object result = _converter.Convert(null!, typeof(Visibility), null!, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void Convert_NonBoolType_ReturnsCollapsed()
    {
        object result = _converter.Convert("true", typeof(Visibility), null!, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void ConvertBack_Visible_ReturnsTrue()
    {
        object result = _converter.ConvertBack(Visibility.Visible, typeof(bool), null!, CultureInfo.InvariantCulture);

        Assert.Equal(true, result);
    }

    [Fact]
    public void ConvertBack_Collapsed_ReturnsFalse()
    {
        object result = _converter.ConvertBack(Visibility.Collapsed, typeof(bool), null!, CultureInfo.InvariantCulture);

        Assert.Equal(false, result);
    }

    [Fact]
    public void ConvertBack_Hidden_ReturnsFalse()
    {
        object result = _converter.ConvertBack(Visibility.Hidden, typeof(bool), null!, CultureInfo.InvariantCulture);

        Assert.Equal(false, result);
    }

    [Fact]
    public void ConvertBack_NonVisibilityType_ReturnsFalse()
    {
        object result = _converter.ConvertBack("Visible", typeof(bool), null!, CultureInfo.InvariantCulture);

        Assert.Equal(false, result);
    }
}

public class StatusToColorConverterTests
{
    // Theme resources aren't loaded in unit tests, so the converter returns its fallback
    // SolidColorBrush directly. We compare on the underlying Color to assert the bucket
    // chosen — Success (green), Error (red), Warning (yellow), or Disabled (grey).
    private static readonly System.Windows.Media.Color SuccessColor = System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80);
    private static readonly System.Windows.Media.Color ErrorColor = System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71);
    private static readonly System.Windows.Media.Color DisabledColor = System.Windows.Media.Color.FromRgb(0xA8, 0xAA, 0xC0);

    private readonly StatusToColorConverter _converter = new();

    private System.Windows.Media.Color ConvertToColor(string status) =>
        ((System.Windows.Media.SolidColorBrush)_converter.Convert(status, typeof(System.Windows.Media.Brush), null!, CultureInfo.InvariantCulture)).Color;

    [Fact]
    public void Convert_FailedPrefix_PicksError()
    {
        // Regression: raw exception messages like "The SSL connection could not be
        // established..." used to paint green because no rule matched and the default
        // was Success. The VM now prefixes failure rows with "Failed: " — assert that
        // trips the red rule.
        Assert.Equal(ErrorColor, ConvertToColor("Failed: The SSL connection could not be established, see inner exception."));
    }

    [Fact]
    public void Convert_ErrorPrefix_PicksError()
    {
        Assert.Equal(ErrorColor, ConvertToColor("Error: socket closed"));
    }

    [Fact]
    public void Convert_PremiumStatus_PicksSuccess()
    {
        Assert.Equal(SuccessColor, ConvertToColor("Premium until 2099-01-01"));
    }

    [Fact]
    public void Convert_UnknownText_FallsBackToDisabledGreyNotGreen()
    {
        // The original bug: any status string the converter didn't recognise was painted
        // green (default fallback = SuccessBrush), silently lying about failure. The
        // fix changed the fallback to the neutral disabled colour — better honest grey
        // than misleading green.
        Assert.Equal(DisabledColor, ConvertToColor("Some untranslated novel status string"));
    }
}

public class SingleLineConverterTests
{
    private readonly SingleLineConverter _converter = new();

    [Fact]
    public void Convert_StringWithCrLf_ReturnsSpacesInsteadOfLineBreaks()
    {
        object result = _converter.Convert("a\r\nb\nc\rd", typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal("a b c d", result);
    }

    [Fact]
    public void Convert_NullOrEmpty_ReturnsValueUnchanged()
    {
        Assert.Null(_converter.Convert(null!, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, _converter.Convert(string.Empty, typeof(string), null!, CultureInfo.InvariantCulture));
    }
}
