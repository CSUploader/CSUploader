// <copyright file="Localizer.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace CSUploader.Lib.Localization;

/// <summary>
/// Singleton localisation source for the WPF UI. Bindings target the indexer
/// <c>[Localizer.Instance][key]</c>; when <see cref="Culture"/> is reassigned the
/// instance raises <see cref="INotifyPropertyChanged.PropertyChanged"/> for the indexer
/// so every <c>{loc:T Key}</c> binding re-evaluates and the visible UI updates in
/// place — no restart needed (already-open dialogs hold whatever they captured at
/// construction).
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    /// <summary>
    /// Process-wide singleton. WPF bindings reach this via <c>{x:Static loc:Localizer.Instance}</c>
    /// inside the <see cref="Markup.LocExtension"/> markup extension.
    /// </summary>
    public static Localizer Instance { get; } = new();

    private readonly ResourceManager _resourceManager;
    private CultureInfo _culture = CultureInfo.CurrentUICulture;

    private Localizer()
    {
        // Resources/Strings.resx, Strings.zh-Hans.resx, Strings.ko.resx, Strings.ja.resx
        // all compile to satellite assemblies addressed by this base name.
        _resourceManager = new ResourceManager("CSUploader.Resources.Strings", typeof(Localizer).Assembly);
    }

    /// <summary>
    /// Active UI culture. Reassign to switch language live; bindings re-fetch on the
    /// next layout pass. Falls back to invariant if the requested culture has no
    /// satellite assembly (English baseline ResX is the neutral set).
    /// </summary>
    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            if (Equals(_culture, value))
            {
                return;
            }

            _culture = value;
            // PropertyChanged with empty / "Item[]" tells every binding "anything could
            // have changed" — WPF re-evaluates indexer-based bindings on this signal.
            PropertyChanged?.Invoke(this, AllItemsChanged);
            PropertyChanged?.Invoke(this, AllPropertiesChanged);
        }
    }

    /// <summary>
    /// Looks up a string by its ResX key. Returns the English neutral value when the
    /// active culture has no entry, and the key itself if even the neutral set lacks
    /// the row (so a missing translation is visible in the UI rather than blank).
    /// </summary>
    public string this[string key]
    {
        get
        {
            try
            {
                return _resourceManager.GetString(key, _culture) ?? key;
            }
            catch (MissingManifestResourceException)
            {
                // Strings.resx isn't compiled yet (e.g. design-time preview). Return the
                // key so the UI shows something rather than crashing.
                return key;
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static readonly PropertyChangedEventArgs AllItemsChanged = new("Item[]");
    private static readonly PropertyChangedEventArgs AllPropertiesChanged = new(string.Empty);

    /// <summary>
    /// BCP-47 tags for the languages we ship satellite assemblies for. The neutral
    /// (English) baseline is the empty string — i.e. resources resolved from the main
    /// assembly without a culture suffix.
    /// </summary>
    public static IReadOnlyList<string> SupportedLanguages { get; } = ["en", "zh-Hans", "ko", "ja", "vi", "fil"];

    /// <summary>
    /// Maps a saved language tag (or the auto-detected OS culture) to the closest
    /// shipped language. Returns "en" as the baseline when nothing matches.
    /// </summary>
    public static string PickSupportedLanguage(string? saved, CultureInfo? osCulture = null)
    {
        if (!string.IsNullOrWhiteSpace(saved) && SupportedLanguages.Contains(saved, StringComparer.OrdinalIgnoreCase))
        {
            return SupportedLanguages.First(l => string.Equals(l, saved, StringComparison.OrdinalIgnoreCase));
        }

        // Auto-detect: walk the OS culture's parent chain, matching by short name.
        // zh-CN / zh-Hans-CN / zh → "zh-Hans" (we only ship Simplified). ja-JP → "ja".
        // ko-KR → "ko". vi-VN → "vi". fil-PH / tl-PH → "fil". Anything else → "en".
        CultureInfo? culture = osCulture ?? CultureInfo.CurrentUICulture;
        while (culture is not null && !culture.Equals(CultureInfo.InvariantCulture))
        {
            string name = culture.Name;
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return "zh-Hans";
            }

            if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            {
                return "ja";
            }

            if (name.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            {
                return "ko";
            }

            if (name.StartsWith("vi", StringComparison.OrdinalIgnoreCase))
            {
                return "vi";
            }

            // "tl" (Tagalog) is the historic ISO 639-1 code that Windows/CLDR still emit
            // for Philippine locales; "fil" is the modern preferred form. Fold both into
            // the "fil" satellite assembly we ship.
            if (name.StartsWith("fil", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("tl", StringComparison.OrdinalIgnoreCase))
            {
                return "fil";
            }

            culture = culture.Parent;
        }

        return "en";
    }
}
