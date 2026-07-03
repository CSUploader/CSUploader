// <copyright file="ConverterTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows;
using CSUploader.Converters;
using CSUploader.Dal;
using CSUploader.Lib.Net;
using CSUploader.Upload;

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

public class StorageAvailableDisplayMultiConverterTests
{
    private readonly StorageAvailableDisplayMultiConverter _converter = new();

    private object Convert(long? used, long? quota, string? hosterName = null)
        => _converter.Convert([BoxOrNull(used), BoxOrNull(quota), hosterName!], typeof(string), null!, CultureInfo.InvariantCulture);

    // Mirror what a MultiBinding hands the converter for a null long? source: the binding
    // contributes a null array slot, not a boxed default.
    private static object BoxOrNull(long? v) => v is long l ? l : null!;

    [Fact]
    public void Convert_KnownQuota_RendersRemainingBytes()
    {
        // used + quota both known → Available = quota - used, formatted in binary IEC.
        // 1 GiB used of 10 GiB → 9 GiB remaining ≈ "9 GiB".
        object result = Convert(used: 1L * 1024 * 1024 * 1024, quota: 10L * 1024 * 1024 * 1024);

        string text = Assert.IsType<string>(result);
        Assert.Contains("GiB", text, StringComparison.Ordinal);
        Assert.StartsWith("9", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_OverQuota_ClampsAtZero()
    {
        // Mirror StorageAvailableBytes: never a negative value.
        object result = Convert(used: 100L, quota: 50L);

        string text = Assert.IsType<string>(result);
        Assert.StartsWith("0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_UsedKnownButNoQuota_RendersUnlimited()
    {
        // Ex-Load shape: storage reported (used known) but no cap (quota null) → "Unlimited"
        // rather than a blank cell.
        object result = Convert(used: 5L, quota: null);

        string text = Assert.IsType<string>(result);
        Assert.False(string.IsNullOrEmpty(text), "Unlimited storage must render a non-empty label");
        // Not a byte-formatted value (no IEC unit suffix) — it's the localized word.
        Assert.DoesNotContain("iB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_NoStorageInfo_RendersDash()
    {
        // Neither used nor quota known, and not a known-unlimited hoster → we couldn't retrieve it: "-".
        object result = Convert(used: null, quota: null, hosterName: "Rapidgator");

        Assert.Equal("-", result);
    }

    [Fact]
    public void Convert_KnownUnlimitedHosterWithNoUsage_RendersUnlimited()
    {
        // catbox reports neither used nor quota but IS unlimited → "Unlimited", not "-".
        object result = Convert(used: null, quota: null, hosterName: "Catbox");

        string text = Assert.IsType<string>(result);
        Assert.NotEqual("-", text);
        Assert.False(string.IsNullOrEmpty(text));
        Assert.DoesNotContain("iB", text, StringComparison.Ordinal); // the localized word, not bytes
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => _converter.ConvertBack("x", [typeof(long), typeof(long)], null!, CultureInfo.InvariantCulture));
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

public class UrlDecodeConverterTests
{
    private readonly UrlDecodeConverter _converter = new();

    private string Convert(object value)
        => (string)_converter.Convert(value, typeof(string), null!, CultureInfo.InvariantCulture);

    [Fact]
    public void Convert_PercentEncodedString_Decodes()
    {
        Assert.Equal(
            "https://a.com/b c",
            Convert("https%3A%2F%2Fa.com%2Fb%20c"));
    }

    [Fact]
    public void Convert_PlainString_ReturnedUnchanged()
    {
        Assert.Equal("https://a.com/already/plain", Convert("https://a.com/already/plain"));
    }

    [Fact]
    public void Convert_Null_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, Convert(null!));
    }

    [Fact]
    public void Convert_EmptyString_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, Convert(string.Empty));
    }

    [Fact]
    public void Convert_NonStringType_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, Convert(12345));
    }

    [Fact]
    public void Convert_MalformedPercentSequence_ReturnsOriginalWithoutThrowing()
    {
        // A dangling '%' is not a valid escape; Uri.UnescapeDataString tolerates some
        // malformed input but the converter must never throw — it falls back to the input.
        const string malformed = "https://a.com/%ZZ%/x";
        Assert.Equal(malformed, Convert(malformed));
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(
            () => _converter.ConvertBack("x", typeof(string), null!, CultureInfo.InvariantCulture));
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

public class AccountCheckStatusToColorConverterTests
{
    // Theme resources aren't loaded in unit tests, so the converter returns its fallback
    // SolidColorBrush directly. We compare on the underlying Color to assert the bucket
    // chosen — Success (green), Error (red), Warning (yellow), or Disabled (grey).
    private static readonly System.Windows.Media.Color SuccessColor = System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80);
    private static readonly System.Windows.Media.Color ErrorColor = System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71);
    private static readonly System.Windows.Media.Color WarningColor = System.Windows.Media.Color.FromRgb(0xFB, 0xBF, 0x24);
    private static readonly System.Windows.Media.Color DisabledColor = System.Windows.Media.Color.FromRgb(0xA8, 0xAA, 0xC0);

    private readonly AccountCheckStatusToColorConverter _converter = new();

    private System.Windows.Media.Color ConvertToColor(object value) =>
        ((System.Windows.Media.SolidColorBrush)_converter.Convert(value, typeof(System.Windows.Media.Brush), null!, CultureInfo.InvariantCulture)).Color;

    [Theory]
    [InlineData(AccountCheckStatus.Valid)]
    public void Convert_Valid_PicksSuccess(AccountCheckStatus s) => Assert.Equal(SuccessColor, ConvertToColor(s));

    [Theory]
    [InlineData(AccountCheckStatus.Failed)]
    public void Convert_Failed_PicksError(AccountCheckStatus s) => Assert.Equal(ErrorColor, ConvertToColor(s));

    [Theory]
    [InlineData(AccountCheckStatus.Checking)]
    public void Convert_Checking_PicksWarning(AccountCheckStatus s) => Assert.Equal(WarningColor, ConvertToColor(s));

    [Theory]
    [InlineData(AccountCheckStatus.NotChecked)]
    [InlineData(AccountCheckStatus.Unsupported)]
    public void Convert_NeutralStates_PickDisabled(AccountCheckStatus s) => Assert.Equal(DisabledColor, ConvertToColor(s));

    [Fact]
    public void Convert_NonEnumInput_FallsBackToDisabledGreyInsteadOfThrowing()
    {
        // Regression: the previous string-sniffing converter painted any unrecognised
        // text green because its catch-all fallback was SuccessBrush. The enum-based
        // replacement defaults non-enum input (null at design-time, wrong binding type)
        // to grey — better an honest "no opinion" than a misleading "OK".
        Assert.Equal(DisabledColor, ConvertToColor("not an enum value"));
        Assert.Equal(DisabledColor, ConvertToColor(null!));
    }
}

public class ItemStateToVisibilityConverterTests
{
    private readonly ItemStateToVisibilityConverter _converter = new();

    [Theory]
    // Startable: only files not yet in the pipeline.
    [InlineData(FileState.Idle)]
    [InlineData(FileState.Paused)]
    [InlineData(FileState.Failed)]
    [InlineData(FileState.Cancelled)]
    public void Startable_NotInPipeline_Visible(FileState state)
        => Assert.Equal(Visibility.Visible, Convert(state, "Startable"));

    [Theory]
    [InlineData(FileState.HashQueued)]
    [InlineData(FileState.UploadQueued)]
    [InlineData(FileState.Hashing)]
    [InlineData(FileState.Uploading)]
    [InlineData(FileState.Completed)]
    public void Startable_QueuedRunningOrDone_Collapsed(FileState state)
        => Assert.Equal(Visibility.Collapsed, Convert(state, "Startable"));

    [Theory]
    // ForceStartable: any row that isn't currently running — incl. queued-and-waiting AND finished
    // (Completed/CompletedWithErrors), which re-upload after a confirmation.
    [InlineData(FileState.Idle)]
    [InlineData(FileState.Paused)]
    [InlineData(FileState.Failed)]
    [InlineData(FileState.Cancelled)]
    [InlineData(FileState.HashQueued)]
    [InlineData(FileState.UploadQueued)]
    [InlineData(FileState.Completed)]
    [InlineData(FileState.CompletedWithErrors)]
    public void ForceStartable_NotRunning_Visible(FileState state)
        => Assert.Equal(Visibility.Visible, Convert(state, "ForceStartable"));

    [Theory]
    [InlineData(FileState.Hashing)]
    [InlineData(FileState.Uploading)]
    public void ForceStartable_Running_Collapsed(FileState state)
        => Assert.Equal(Visibility.Collapsed, Convert(state, "ForceStartable"));

    [Theory]
    // Stoppable (default mode): files currently in the pipeline.
    [InlineData(FileState.Hashing)]
    [InlineData(FileState.Uploading)]
    [InlineData(FileState.HashQueued)]
    [InlineData(FileState.UploadQueued)]
    public void Stoppable_InPipeline_Visible(FileState state)
        => Assert.Equal(Visibility.Visible, Convert(state, "Stoppable"));

    [Theory]
    [InlineData(FileState.Idle)]
    [InlineData(FileState.Paused)]
    [InlineData(FileState.Completed)]
    public void Stoppable_NotInPipeline_Collapsed(FileState state)
        => Assert.Equal(Visibility.Collapsed, Convert(state, "Stoppable"));

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
        => Assert.Throws<NotSupportedException>(
            () => _converter.ConvertBack(Visibility.Visible, typeof(object), "Startable", CultureInfo.InvariantCulture));

    private Visibility Convert(FileState state, string mode)
        => (Visibility)_converter.Convert(FileInState(state), typeof(Visibility), mode, CultureInfo.InvariantCulture);

    private static PackageFile FileInState(FileState state)
    {
        FileHosterClient hoster = new("Rapidgator", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "Rapidgator", IsAnonymous = true };
        Package package = new(new PackageOptions { Title = "t", FileHosters = new() { { hoster, login } } });
        // The source file need not exist — FileInfo is lazy and the converter only reads State.
        return new PackageFile(package, @"C:\nonexistent\x.bin", hoster, login) { State = state };
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
