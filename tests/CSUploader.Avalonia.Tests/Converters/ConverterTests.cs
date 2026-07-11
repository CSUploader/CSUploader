// <copyright file="ConverterTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia;
using Avalonia.Controls; // ResourceNodeExtensions.TryFindResource
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CSUploader.Converters;
using CSUploader.Dal;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using CSUploader.ViewModels;

namespace CSUploader.Tests.Avalonia.Converters;

// Mirrors tests/Converters/ConverterTests.cs (WPF) with the Task 6 port deltas: Visibility asserts
// become bool; BoolToVisibilityConverterTests is dropped (retired class) and replaced with
// InvertBoolConverterTests; the resource-resolving converters (which the WPF suite never unit-tested)
// gain [AvaloniaFact] coverage that asserts SAME-instance resolution against the real merged
// resource dictionaries — possible now that the headless session boots the real App.

public class ByteUnitConverterTests
{
    private readonly ByteUnitConverter _converter = new();

    [Fact]
    public void Convert_LongBytes_ReturnsFriendlyString()
    {
        object? result = _converter.Convert(1024L, typeof(string), null, CultureInfo.InvariantCulture);

        string text = Assert.IsType<string>(result);
        Assert.Contains("KiB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_ZeroBytes_ReturnsBString()
    {
        object? result = _converter.Convert(0L, typeof(string), null, CultureInfo.InvariantCulture);

        string text = Assert.IsType<string>(result);
        Assert.Contains("B", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_LargeBytes_ReturnsMiBOrHigher()
    {
        long fiveMiB = 5L * 1024 * 1024;
        object? result = _converter.Convert(fiveMiB, typeof(string), null, CultureInfo.InvariantCulture);

        string text = Assert.IsType<string>(result);
        Assert.Contains("MiB", text, StringComparison.Ordinal);
        Assert.StartsWith("5", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_Null_ReturnsEmptyString()
        => Assert.Equal(string.Empty, _converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_NonLongType_ReturnsEmptyString()
        => Assert.Equal(string.Empty, _converter.Convert("not a long", typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_UnknownPlaceholderParameter_IsHonoured()
        => Assert.Equal("-", _converter.Convert(null, typeof(string), "-", CultureInfo.InvariantCulture));

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
        => Assert.Throws<NotSupportedException>(
            () => _converter.ConvertBack("1 KiB", typeof(long), null, CultureInfo.InvariantCulture));
}

public class StorageAvailableDisplayMultiConverterTests
{
    private readonly StorageAvailableDisplayMultiConverter _converter = new();

    // Mirror what a MultiBinding hands the converter: a null long? source contributes a null slot.
    private object? Convert(long? used, long? quota, string? hosterName = null)
        => _converter.Convert([BoxOrNull(used), BoxOrNull(quota), hosterName], typeof(string), null, CultureInfo.InvariantCulture);

    private static object? BoxOrNull(long? v) => v is long l ? l : null;

    [Fact]
    public void Convert_KnownQuota_RendersRemainingBytes()
    {
        object? result = Convert(used: 1L * 1024 * 1024 * 1024, quota: 10L * 1024 * 1024 * 1024);

        string text = Assert.IsType<string>(result);
        Assert.Contains("GiB", text, StringComparison.Ordinal);
        Assert.StartsWith("9", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_OverQuota_ClampsAtZero()
    {
        object? result = Convert(used: 100L, quota: 50L);

        string text = Assert.IsType<string>(result);
        Assert.StartsWith("0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_UsedKnownButNoQuota_RendersUnlimited()
    {
        object? result = Convert(used: 5L, quota: null);

        string text = Assert.IsType<string>(result);
        Assert.False(string.IsNullOrEmpty(text), "Unlimited storage must render a non-empty label");
        Assert.DoesNotContain("iB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_NoStorageInfo_RendersDash()
        => Assert.Equal("-", Convert(used: null, quota: null, hosterName: "Rapidgator"));

    [Fact]
    public void Convert_KnownUnlimitedHosterWithNoUsage_RendersUnlimited()
    {
        object? result = Convert(used: null, quota: null, hosterName: "Catbox");

        string text = Assert.IsType<string>(result);
        Assert.NotEqual("-", text);
        Assert.False(string.IsNullOrEmpty(text));
        Assert.DoesNotContain("iB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_UnsetSlots_TreatedAsNotProvided()
    {
        // An unset MultiBinding slot arrives as AvaloniaProperty.UnsetValue, not null — it must fail the
        // `is long` match exactly like null does, so "no info + not unlimited" still renders "-".
        object? result = _converter.Convert(
            [AvaloniaProperty.UnsetValue, AvaloniaProperty.UnsetValue, "Rapidgator"],
            typeof(string),
            null,
            CultureInfo.InvariantCulture);

        Assert.Equal("-", result);
    }
}

public class TimeSpanFormatConverterTests
{
    private readonly TimeSpanFormatConverter _converter = new();

    [Fact]
    public void Convert_HoursMinutesSeconds_FormatsWithH()
        => Assert.Equal("2h:30m:45s", _converter.Convert(new TimeSpan(2, 30, 45), typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_MinutesAndSeconds_FormatsWithM()
        => Assert.Equal("15m:30s", _converter.Convert(new TimeSpan(0, 15, 30), typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_SecondsOnly_FormatsWithS()
        => Assert.Equal("42s", _converter.Convert(new TimeSpan(0, 0, 42), typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_ZeroTimeSpan_ReturnsZeroSeconds()
        => Assert.Equal("00s", _converter.Convert(TimeSpan.Zero, typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_Null_ReturnsEmptyString()
        => Assert.Equal(string.Empty, _converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_NonTimeSpanType_ReturnsEmptyString()
        => Assert.Equal(string.Empty, _converter.Convert(12345, typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
        => Assert.Throws<NotSupportedException>(
            () => _converter.ConvertBack("15m:30s", typeof(TimeSpan), null, CultureInfo.InvariantCulture));
}

public class DateTimeFormatConverterTests
{
    private readonly DateTimeFormatConverter _converter = new();

    [Fact]
    public void Convert_DateTime_FormatsCorrectly()
        => Assert.Equal("2025/03/15 14:30:45", _converter.Convert(new DateTime(2025, 3, 15, 14, 30, 45), typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_MidnightDateTime_FormatsWithZeroes()
        => Assert.Equal("2024/01/01 00:00:00", _converter.Convert(new DateTime(2024, 1, 1, 0, 0, 0), typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_MinValue_ReturnsEmptyString()
        => Assert.Equal(string.Empty, _converter.Convert(DateTime.MinValue, typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_Null_ReturnsEmptyString()
        => Assert.Equal(string.Empty, _converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_NonDateTimeType_ReturnsEmptyString()
        => Assert.Equal(string.Empty, _converter.Convert("not a datetime", typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
        => Assert.Throws<NotSupportedException>(
            () => _converter.ConvertBack("2025/03/15 14:30:45", typeof(DateTime), null, CultureInfo.InvariantCulture));
}

public class UrlDecodeConverterTests
{
    private readonly UrlDecodeConverter _converter = new();

    private string Convert(object? value)
        => Assert.IsType<string>(_converter.Convert(value, typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_PercentEncodedString_Decodes()
        => Assert.Equal("https://a.com/b c", Convert("https%3A%2F%2Fa.com%2Fb%20c"));

    [Fact]
    public void Convert_PlainString_ReturnedUnchanged()
        => Assert.Equal("https://a.com/already/plain", Convert("https://a.com/already/plain"));

    [Fact]
    public void Convert_Null_ReturnsEmptyString() => Assert.Equal(string.Empty, Convert(null));

    [Fact]
    public void Convert_EmptyString_ReturnsEmptyString() => Assert.Equal(string.Empty, Convert(string.Empty));

    [Fact]
    public void Convert_NonStringType_ReturnsEmptyString() => Assert.Equal(string.Empty, Convert(12345));

    [Fact]
    public void Convert_MalformedPercentSequence_ReturnsOriginalWithoutThrowing()
    {
        const string malformed = "https://a.com/%ZZ%/x";
        Assert.Equal(malformed, Convert(malformed));
    }

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
        => Assert.Throws<NotSupportedException>(
            () => _converter.ConvertBack("x", typeof(string), null, CultureInfo.InvariantCulture));
}

// Replaces BoolToVisibilityConverterTests — the retired class's role folds into InvertBoolConverter
// (the ConverterParameter=Invert form) + the IsVisible bool binding rule.
public class InvertBoolConverterTests
{
    private readonly InvertBoolConverter _converter = new();

    private object? Convert(object? value) => _converter.Convert(value, typeof(bool), null, CultureInfo.InvariantCulture);

    private object? ConvertBack(object? value) => _converter.ConvertBack(value, typeof(bool), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Convert_True_ReturnsFalse() => Assert.False(Assert.IsType<bool>(Convert(true)));

    [Fact]
    public void Convert_False_ReturnsTrue() => Assert.True(Assert.IsType<bool>(Convert(false)));

    [Fact]
    public void ConvertBack_True_ReturnsFalse() => Assert.False(Assert.IsType<bool>(ConvertBack(true)));

    [Fact]
    public void ConvertBack_False_ReturnsTrue() => Assert.True(Assert.IsType<bool>(ConvertBack(false)));

    [Fact]
    public void Convert_NonBool_PassesThrough()
    {
        Assert.Equal("x", Convert("x"));
        Assert.Null(Convert(null));
    }
}

public class AccountCheckStatusToColorConverterTests
{
    private readonly AccountCheckStatusToColorConverter _converter = new();

    private object? Convert(object? value) => _converter.Convert(value, typeof(IBrush), null, CultureInfo.InvariantCulture);

    // The brush the converter SHOULD resolve: same variant-scoped lookup the converter itself does, so a
    // match pins converter ↔ ThemeBrushes.axaml wiring (the WPF fallback-color tests couldn't).
    private static IBrush Resolved(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, Application.Current!.ActualThemeVariant, out object? v));
        return Assert.IsAssignableFrom<IBrush>(v);
    }

    [AvaloniaFact]
    public void Valid_ResolvesSuccessBrushFromTheActiveVariant()
        => Assert.Same(Resolved("SuccessBrush"), Convert(AccountCheckStatus.Valid));

    [AvaloniaFact]
    public void Failed_ResolvesErrorBrush() => Assert.Same(Resolved("ErrorBrush"), Convert(AccountCheckStatus.Failed));

    [AvaloniaFact]
    public void Checking_ResolvesWarningBrush() => Assert.Same(Resolved("WarningBrush"), Convert(AccountCheckStatus.Checking));

    [AvaloniaFact]
    public void NeutralAndNonEnumInputs_ResolveTheDisabledBrush()
    {
        Assert.Same(Resolved("TextDisabledBrush"), Convert(AccountCheckStatus.NotChecked));
        Assert.Same(Resolved("TextDisabledBrush"), Convert(AccountCheckStatus.Unsupported));
        Assert.Same(Resolved("TextDisabledBrush"), Convert("not an enum value"));
        Assert.Same(Resolved("TextDisabledBrush"), Convert(null));
    }
}

public class ItemStateToVisibilityConverterTests
{
    private readonly ItemStateToVisibilityConverter _converter = new();

    private bool Convert(FileState state, string mode)
        => Assert.IsType<bool>(_converter.Convert(Sample.FileInState(state), typeof(bool), mode, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(FileState.Idle)]
    [InlineData(FileState.Paused)]
    [InlineData(FileState.Failed)]
    [InlineData(FileState.Cancelled)]
    public void Startable_NotInPipeline_Visible(FileState state) => Assert.True(Convert(state, "Startable"));

    [Theory]
    [InlineData(FileState.HashQueued)]
    [InlineData(FileState.UploadQueued)]
    [InlineData(FileState.Hashing)]
    [InlineData(FileState.Uploading)]
    [InlineData(FileState.Completed)]
    public void Startable_QueuedRunningOrDone_Collapsed(FileState state) => Assert.False(Convert(state, "Startable"));

    [Theory]
    [InlineData(FileState.Idle)]
    [InlineData(FileState.Paused)]
    [InlineData(FileState.Failed)]
    [InlineData(FileState.Cancelled)]
    [InlineData(FileState.HashQueued)]
    [InlineData(FileState.UploadQueued)]
    [InlineData(FileState.Completed)]
    [InlineData(FileState.CompletedWithErrors)]
    public void ForceStartable_NotRunning_Visible(FileState state) => Assert.True(Convert(state, "ForceStartable"));

    [Theory]
    [InlineData(FileState.Hashing)]
    [InlineData(FileState.Uploading)]
    public void ForceStartable_Running_Collapsed(FileState state) => Assert.False(Convert(state, "ForceStartable"));

    [Theory]
    [InlineData(FileState.Hashing)]
    [InlineData(FileState.Uploading)]
    [InlineData(FileState.HashQueued)]
    [InlineData(FileState.UploadQueued)]
    public void Stoppable_InPipeline_Visible(FileState state) => Assert.True(Convert(state, "Stoppable"));

    [Theory]
    [InlineData(FileState.Idle)]
    [InlineData(FileState.Paused)]
    [InlineData(FileState.Completed)]
    public void Stoppable_NotInPipeline_Collapsed(FileState state) => Assert.False(Convert(state, "Stoppable"));

    [Fact]
    public void ConvertBack_ThrowsNotSupportedException()
        => Assert.Throws<NotSupportedException>(
            () => _converter.ConvertBack(true, typeof(object), "Startable", CultureInfo.InvariantCulture));
}

public class SingleLineConverterTests
{
    private readonly SingleLineConverter _converter = new();

    [Fact]
    public void Convert_StringWithCrLf_ReturnsSpacesInsteadOfLineBreaks()
        => Assert.Equal("a b c d", _converter.Convert("a\r\nb\nc\rd", typeof(string), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_NullOrEmpty_ReturnsValueUnchanged()
    {
        Assert.Null(_converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, _converter.Convert(string.Empty, typeof(string), null, CultureInfo.InvariantCulture));
    }
}

public class StepConvertersTests
{
    private readonly StepVisibilityConverter _visibility = new();
    private readonly StepFontConverter _font = new();

    [Fact]
    public void StepVisibility_CurrentEqualsStep_True()
        => Assert.True(Assert.IsType<bool>(_visibility.Convert(2, typeof(bool), "2", CultureInfo.InvariantCulture)));

    [Fact]
    public void StepVisibility_CurrentDiffersFromStep_False()
        => Assert.False(Assert.IsType<bool>(_visibility.Convert(1, typeof(bool), "2", CultureInfo.InvariantCulture)));

    [Fact]
    public void StepVisibility_NonIntValue_False()
        => Assert.False(Assert.IsType<bool>(_visibility.Convert("x", typeof(bool), "2", CultureInfo.InvariantCulture)));

    [Fact]
    public void StepFont_CurrentEqualsStep_Bold()
        => Assert.Equal(FontWeight.Bold, Assert.IsType<FontWeight>(_font.Convert(2, typeof(FontWeight), "2", CultureInfo.InvariantCulture)));

    [Fact]
    public void StepFont_CurrentDiffersFromStep_Normal()
        => Assert.Equal(FontWeight.Normal, Assert.IsType<FontWeight>(_font.Convert(1, typeof(FontWeight), "2", CultureInfo.InvariantCulture)));
}

public class EnumBoolConverterTests
{
    private readonly EnumBoolConverter _converter = new();

    [Fact]
    public void Convert_ValueMatchesParameter_True()
        => Assert.True(Assert.IsType<bool>(_converter.Convert(FileState.Uploading, typeof(bool), "Uploading", CultureInfo.InvariantCulture)));

    [Fact]
    public void Convert_ValueDiffersFromParameter_False()
        => Assert.False(Assert.IsType<bool>(_converter.Convert(FileState.Uploading, typeof(bool), "Paused", CultureInfo.InvariantCulture)));

    [Fact]
    public void Convert_NullValueOrMissingParameter_False()
    {
        Assert.False(Assert.IsType<bool>(_converter.Convert(null, typeof(bool), "Uploading", CultureInfo.InvariantCulture)));
        Assert.False(Assert.IsType<bool>(_converter.Convert(FileState.Uploading, typeof(bool), null, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void ConvertBack_True_ReturnsParsedEnumMember()
        => Assert.Equal(FileState.Uploading, _converter.ConvertBack(true, typeof(FileState), "Uploading", CultureInfo.InvariantCulture));

    [Fact]
    public void ConvertBack_False_ReturnsDoNothing()
        => Assert.Same(BindingOperations.DoNothing, _converter.ConvertBack(false, typeof(FileState), "Uploading", CultureInfo.InvariantCulture));
}

public class SpeedLimitConverterTests
{
    private readonly SpeedLimitConverter _converter = new();

    private object? Convert(int kbps) => _converter.Convert(kbps, typeof(string), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Convert_1024Kbps_RendersOneMegabytePerSecond() => Assert.Equal("1 MB/s", Convert(1024));

    [Fact]
    public void Convert_SubMegabyte_RendersKilobytesPerSecond() => Assert.Equal("512 KB/s", Convert(512));

    [Fact]
    public void Convert_Zero_RendersEmpty() => Assert.Equal(string.Empty, Convert(0));
}

public class ProgressWidthConverterTests
{
    private readonly ProgressWidthConverter _converter = new();

    private double Convert(params object?[] values)
        => Assert.IsType<double>(_converter.Convert(values, typeof(double), null, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_HalfProgressOf200_Returns100() => Assert.Equal(100.0, Convert(50.0, 200.0));

    [Fact]
    public void Convert_OverHundredPercent_ClampsToFullWidth() => Assert.Equal(200.0, Convert(150.0, 200.0));

    [Fact]
    public void Convert_UnsetSlot_ReturnsZero() => Assert.Equal(0.0, Convert(AvaloniaProperty.UnsetValue, 200.0));

    [Fact]
    public void Convert_ZeroWidth_ReturnsZero() => Assert.Equal(0.0, Convert(50.0, 0.0));
}

public class FileStateIconConverterTests
{
    private readonly FileStateIconConverter _converter = new();

    private object? Convert(object? value) => _converter.Convert(value, typeof(Bitmap), null, CultureInfo.InvariantCulture);

    [AvaloniaFact]
    public void Convert_Uploading_ResolvesTheUploadingBitmapInstance()
        => Assert.Same(ConverterResources.Bitmap("StatusUploadingImage"), Convert(FileState.Uploading));

    [AvaloniaFact]
    public void Convert_Completed_ResolvesTheSuccessBitmap()
        => Assert.Same(ConverterResources.Bitmap("StatusSuccessImage"), Convert(FileState.Completed));

    [AvaloniaFact]
    public void Convert_NonEnum_ReturnsNull() => Assert.Null(Convert("not a state"));
}

public class HosterIconConverterTests
{
    private readonly HosterIconConverter _converter = new();

    private object? Convert(object? value) => _converter.Convert(value, typeof(Bitmap), null, CultureInfo.InvariantCulture);

    [AvaloniaFact]
    public void Convert_HyphenatedName_ResolvesTheHyphenStrippedKey()
        // "Ex-Load" → FileHosterExloadImage (spaces + hyphens dropped) — the load-bearing normalization.
        => Assert.Same(ConverterResources.Bitmap("FileHosterExloadImage"), Convert("Ex-Load"));

    [AvaloniaFact]
    public void Convert_PlainName_ResolvesItsIcon()
        => Assert.Same(ConverterResources.Bitmap("FileHosterRapidgatorImage"), Convert("Rapidgator"));

    [AvaloniaFact]
    public void Convert_UnknownHoster_ReturnsNull() => Assert.Null(Convert("No Such Hoster"));

    [AvaloniaFact]
    public void Convert_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(Convert(null));
        Assert.Null(Convert(string.Empty));
    }
}

public class ProxyTestOutcomeIconConverterTests
{
    private readonly ProxyTestOutcomeIconConverter _converter = new();

    private object? Convert(object? value) => _converter.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);

    [AvaloniaFact]
    public void Convert_Ok_ResolvesTheOkIcon()
        => Assert.Same(ConverterResources.Resource("StatusOkImage"), Convert(ProxyTestOutcome.Ok));

    [AvaloniaFact]
    public void Convert_Failed_ResolvesTheFailedIcon()
        => Assert.Same(ConverterResources.Resource("StatusFailedImage"), Convert(ProxyTestOutcome.Failed));

    [AvaloniaFact]
    public void Convert_Untested_ReturnsNull() => Assert.Null(Convert(ProxyTestOutcome.Untested));
}

public class ResourceKeyToImageConverterTests
{
    private readonly ResourceKeyToImageConverter _converter = new();

    private object? Convert(object? value) => _converter.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);

    [AvaloniaFact]
    public void Convert_KnownKey_ResolvesTheResourceInstance()
        => Assert.Same(ConverterResources.Resource("StatusSuccessImage"), Convert("StatusSuccessImage"));

    [AvaloniaFact]
    public void Convert_NullOrWhitespace_ReturnsUnsetValue()
    {
        Assert.Same(AvaloniaProperty.UnsetValue, Convert(null));
        Assert.Same(AvaloniaProperty.UnsetValue, Convert("   "));
    }

    [AvaloniaFact]
    public void Convert_UnknownKey_ReturnsUnsetValue()
        => Assert.Same(AvaloniaProperty.UnsetValue, Convert("NoSuchResourceKey"));
}

public class FileStateDisplayConverterTests
{
    private readonly FileStateDisplayConverter _converter = new();

    [Fact]
    public void Convert_KnownState_ReturnsTheLocalizedStateLabel()
    {
        // Culture-independent: assert the converter returns exactly what the same Localizer key yields,
        // not a hard-coded English string.
        object? result = _converter.Convert(FileState.Uploading, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal(Localizer.Instance["Uploads_State_Uploading"], result);
    }

    [Fact]
    public void Convert_NonEnum_ReturnsEmpty()
        => Assert.Equal(string.Empty, _converter.Convert("not a state", typeof(string), null, CultureInfo.InvariantCulture));
}

public class StartMenuLabelConverterTests
{
    private readonly StartMenuLabelConverter _converter = new();

    private object? Convert(object? value) => _converter.Convert(value, typeof(string), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Convert_NoSchedule_ReturnsTheStartLabel()
        => Assert.Equal(Localizer.Instance["Uploads_Context_Start"], Convert(Sample.Package(scheduledStartTime: null)));

    [Fact]
    public void Convert_FutureSchedule_ReturnsTheStartNowLabel()
        => Assert.Equal(Localizer.Instance["Uploads_Context_StartNow"], Convert(Sample.Package(scheduledStartTime: DateTime.Now.AddHours(1))));

    [Fact]
    public void Convert_UnrelatedValue_FallsBackToTheStartLabel()
        => Assert.Equal(Localizer.Instance["Uploads_Context_Start"], Convert("neither a package nor a file"));
}

// Shared resolution helpers for the [AvaloniaFact] resource-resolving converter tests: resolve the SAME
// instance the converter should return, from the real merged resource dictionaries.
internal static class ConverterResources
{
    internal static Bitmap Bitmap(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out object? v), $"missing resource: {key}");
        return Assert.IsType<Bitmap>(v);
    }

    internal static object Resource(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out object? v), $"missing resource: {key}");
        return v!;
    }
}

// Bogus Package/PackageFile builders for the state/label converters (Core types; compile unchanged from
// the WPF harness's FileInState). The source file need not exist — FileInfo is lazy and the converters
// only read State / ScheduledStartTime.
internal static class Sample
{
    internal static Package Package(DateTime? scheduledStartTime = null)
    {
        FileHosterClient hoster = new("Rapidgator", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "Rapidgator", IsAnonymous = true };
        Package package = new(new PackageOptions { Title = "t", FileHosters = new() { { hoster, login } } });
        package.ScheduledStartTime = scheduledStartTime;
        return package;
    }

    internal static PackageFile FileInState(FileState state)
    {
        FileHosterClient hoster = new("Rapidgator", Protocol.Http);
        FileHosterLoginDto login = new() { FileHosterName = "Rapidgator", IsAnonymous = true };
        Package package = new(new PackageOptions { Title = "t", FileHosters = new() { { hoster, login } } });
        return new PackageFile(package, @"C:\nonexistent\x.bin", hoster, login) { State = state };
    }
}
