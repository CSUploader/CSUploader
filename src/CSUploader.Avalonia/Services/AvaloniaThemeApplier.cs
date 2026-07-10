namespace CSUploader.Services;

/// <summary>
/// Avalonia implementation of <see cref="IThemeApplier"/>. No-op in Phase 2 — the theme/token port
/// is Phase 3, and this type is the designated sole writer of the new-window dark-chrome preference
/// per the design's Phase 7 note. Both members stay inert until then so the shared Settings/Main
/// ViewModels can call them safely.
/// </summary>
public sealed class AvaloniaThemeApplier : IThemeApplier
{
    public void ApplyGridFont(string family, double size)
    {
        // TODO(phase3): push GridFontFamily/GridFontSize into the Avalonia app resource surface so
        // the ported DataGrids' DynamicResource bindings pick up the change live.
    }

    public void ApplyTheme(bool isDark)
    {
        // TODO(phase3): set Application.RequestedThemeVariant to Light/Dark and let the app's
        // ThemeVariant token dictionaries follow — the Avalonia mechanism, NOT a merged-dictionary
        // swap (that is the WPF head's WpfThemeApplier).
        // TODO(phase7): apply the immersive dark window chrome to open/new windows.
    }
}
