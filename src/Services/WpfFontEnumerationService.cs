// <copyright file="WpfFontEnumerationService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows.Media;

namespace CSUploader.Services;

/// <summary>
/// WPF implementation of <see cref="IFontEnumerationService"/>. Projects
/// <see cref="Fonts.SystemFontFamilies"/> into the same de-duplicated, case-insensitively
/// sorted name list the Settings font dropdown used to build inline.
/// </summary>
public sealed class WpfFontEnumerationService : IFontEnumerationService
{
    public IReadOnlyList<string> GetSystemFontFamilyNames() =>
        [.. Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
}
