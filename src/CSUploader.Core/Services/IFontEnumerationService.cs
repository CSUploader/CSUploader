// <copyright file="IFontEnumerationService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Services;

/// <summary>
/// Enumerates the font families installed on the system. The WPF head projects WPF's
/// <c>Fonts.SystemFontFamilies</c>; the Avalonia head supplies its own.
/// Keeps WPF media types out of the shared Settings ViewModel.
/// </summary>
public interface IFontEnumerationService
{
    /// <summary>The installed font-family names, de-duplicated and sorted for display.</summary>
    IReadOnlyList<string> GetSystemFontFamilyNames();
}
