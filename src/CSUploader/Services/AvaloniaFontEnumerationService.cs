using Avalonia.Media;

namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of <see cref="IFontEnumerationService"/>. Projects
/// <see cref="FontManager.SystemFonts"/> (an <see cref="IReadOnlyList{FontFamily}"/>) into the same
/// de-duplicated, case-insensitively sorted name list the WPF head builds from
/// <c>Fonts.SystemFontFamilies</c>.
/// </summary>
public sealed class AvaloniaFontEnumerationService : IFontEnumerationService
{
    public IReadOnlyList<string> GetSystemFontFamilyNames() =>
        [.. FontManager.Current.SystemFonts
            .Select(f => f.Name)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
}
