// <copyright file="DateTimeOffsetConverter.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace CSUploader.Converters;

/// <summary>
/// Bridges Avalonia's <c>DatePicker.SelectedDate</c> (a <see cref="DateTimeOffset"/><c>?</c>) to a
/// non-nullable <see cref="DateTime"/> source property. The WPF <c>DatePicker.SelectedDate</c> is a
/// <c>DateTime?</c>, so the WPF wizard binds <c>ScheduledDate</c> (a non-nullable <see cref="DateTime"/>
/// defaulting to tomorrow) with no converter; Avalonia's <see cref="DateTimeOffset"/><c>?</c> shape needs
/// this shim (Phase 6 port rule 36). The Core view models stay read-only, so the type gap is closed here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Convert</b> (source → picker): a <see cref="DateTime"/> becomes a <see cref="DateTimeOffset"/> built
/// from the clock components with <see cref="DateTimeKind.Unspecified"/>, so the picker shows the same
/// calendar day regardless of the incoming <see cref="DateTime.Kind"/> (a <c>Utc</c> value would otherwise
/// pin a zero offset and could shift the displayed day). Anything that is not a <see cref="DateTime"/>
/// (defensive; the real binding never sends null) yields <c>null</c>.
/// </para>
/// <para>
/// <b>ConvertBack</b> (picker → source) — the load-bearing null-handling choice: a real
/// <see cref="DateTimeOffset"/> returns its <see cref="DateTimeOffset.DateTime"/> clock value; a CLEARED
/// picker (<c>SelectedDate == null</c>) returns <see cref="BindingOperations.DoNothing"/> so the write is
/// aborted and the non-nullable source keeps its last value. Returning <c>default(DateTime)</c> would zero
/// the wizard's tomorrow default; <see cref="BindingOperations.DoNothing"/> is the precise "skip this
/// transfer" sentinel (the same one <c>EnumBoolConverter</c> uses on its RadioButton-uncheck path, for the
/// identical "do not clobber the source" reason) — unlike <c>AvaloniaProperty.UnsetValue</c>, which signals
/// "no value" and can fall through to the property default.
/// </para>
/// </remarks>
public sealed class DateTimeOffsetConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime dt
            ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified))
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTimeOffset dto ? dto.DateTime : BindingOperations.DoNothing;
}
