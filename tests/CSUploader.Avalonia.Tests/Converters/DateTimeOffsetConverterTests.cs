// <copyright file="DateTimeOffsetConverterTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Data;
using CSUploader.Converters;

namespace CSUploader.Tests.Avalonia.Converters;

// Phase 6 Task 2 (port rule 36): the DateTime <-> DateTimeOffset? shim for the wizard's DatePicker.
// The load-bearing case is ConvertBack(null): a cleared picker must NOT clobber the non-nullable source,
// so it returns BindingOperations.DoNothing (the same "leave the source untouched" sentinel EnumBoolConverter
// uses on its RadioButton-uncheck path) rather than default(DateTime) or AvaloniaProperty.UnsetValue.
public class DateTimeOffsetConverterTests
{
    private readonly DateTimeOffsetConverter _converter = new();

    private object? Convert(object? value) => _converter.Convert(value, typeof(DateTimeOffset?), null, CultureInfo.InvariantCulture);

    private object? ConvertBack(object? value) => _converter.ConvertBack(value, typeof(DateTime), null, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(2026, 7, 15)]
    [InlineData(2024, 1, 1)]
    [InlineData(2030, 12, 31)]
    public void Convert_DateTime_PreservesTheClockComponents(int year, int month, int day)
    {
        var dt = new DateTime(year, month, day, 9, 30, 0);

        DateTimeOffset result = Assert.IsType<DateTimeOffset>(Convert(dt));

        // The picker only shows the day, but the whole clock value survives so ConvertBack round-trips.
        Assert.Equal(dt, result.DateTime);
        Assert.Equal(dt.Date, result.Date);
    }

    [Fact]
    public void Convert_UtcKind_StillShowsTheSameCalendarDay()
    {
        // A Utc DateTime must not be pinned to a zero offset (which could shift the displayed day under a
        // non-zero local offset) — SpecifyKind(Unspecified) keeps the clock day the source carries.
        var utc = new DateTime(2026, 7, 15, 0, 30, 0, DateTimeKind.Utc);

        DateTimeOffset result = Assert.IsType<DateTimeOffset>(Convert(utc));

        Assert.Equal(new DateTime(2026, 7, 15), result.Date);
    }

    [Fact]
    public void Convert_Null_ReturnsNull() => Assert.Null(Convert(null));

    [Fact]
    public void Convert_NonDateTime_ReturnsNull() => Assert.Null(Convert("not a date"));

    [Fact]
    public void ConvertBack_DateTimeOffset_ReturnsTheClockDateTime()
    {
        var dto = new DateTimeOffset(2027, 3, 9, 14, 45, 0, TimeSpan.FromHours(2));

        DateTime result = Assert.IsType<DateTime>(ConvertBack(dto));

        Assert.Equal(new DateTime(2027, 3, 9, 14, 45, 0), result);
    }

    [Fact]
    public void ConvertBack_Null_ReturnsDoNothing_SoTheNonNullableSourceIsUntouched()
        => Assert.Same(BindingOperations.DoNothing, ConvertBack(null));

    [Fact]
    public void ConvertBack_NonDateTimeOffset_ReturnsDoNothing()
        => Assert.Same(BindingOperations.DoNothing, ConvertBack("not an offset"));

    [Theory]
    [InlineData(2026, 7, 15)]
    [InlineData(2024, 2, 29)]
    public void RoundTrip_DateTime_IsPreserved(int year, int month, int day)
    {
        var original = new DateTime(year, month, day, 8, 0, 0);

        object? forward = Convert(original);
        DateTime back = Assert.IsType<DateTime>(ConvertBack(forward));

        Assert.Equal(original, back);
    }
}
