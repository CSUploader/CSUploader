// <copyright file="LocalizerTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using System.Globalization;
using CSUploader.Lib.Localization;

namespace CSUploader.Tests.Lib.Localization;

// Localizer.Instance is a process-wide singleton — leaving its Culture mutated bleeds into
// any later test that reads localised strings (e.g. the Settings VM tests). Reset on dispose
// AND join the LocalizerCollection so xUnit serializes us against other classes that touch
// the singleton, otherwise per-test cleanup races against parallel readers.
[Collection(LocalizerCollection.Name)]
public class LocalizerTests : IDisposable
{
    private readonly CultureInfo _originalCulture = Localizer.Instance.Culture;

    public void Dispose()
    {
        Localizer.Instance.Culture = _originalCulture;
        GC.SuppressFinalize(this);
    }


    [Fact]
    public void Indexer_ReturnsTranslatedValue_ForSupportedCulture()
    {
        Localizer.Instance.Culture = new CultureInfo("zh-Hans");

        Assert.Equal("确定", Localizer.Instance["Common_OK"]);
    }

    [Fact]
    public void Indexer_FallsBackToNeutral_WhenKeyMissingInCulture()
    {
        // ja.resx exists but Common_OK is intentionally left as "OK" — fall-through case.
        Localizer.Instance.Culture = new CultureInfo("ja");

        string value = Localizer.Instance["Common_OK"];

        Assert.Equal("OK", value);
    }

    [Fact]
    public void Indexer_ReturnsKey_WhenKeyDoesNotExist()
    {
        Localizer.Instance.Culture = CultureInfo.InvariantCulture;

        string value = Localizer.Instance["__definitely_missing_key__"];

        Assert.Equal("__definitely_missing_key__", value);
    }

    [Fact]
    public void SettingCulture_RaisesPropertyChangedForIndexer()
    {
        Localizer.Instance.Culture = new CultureInfo("en");

        List<string?> raised = [];
        void handler(object? _, PropertyChangedEventArgs e) => raised.Add(e.PropertyName);
        Localizer.Instance.PropertyChanged += handler;

        try
        {
            Localizer.Instance.Culture = new CultureInfo("zh-Hans");
        }
        finally
        {
            Localizer.Instance.PropertyChanged -= handler;
        }

        // WPF re-evaluates indexer bindings on either signal — both are raised.
        Assert.Contains("Item[]", raised);
        Assert.Contains(string.Empty, raised);
    }

    [Fact]
    public void SettingCulture_DoesNotRaise_WhenValueUnchanged()
    {
        CultureInfo culture = new("en");
        Localizer.Instance.Culture = culture;

        int count = 0;
        void handler(object? _, PropertyChangedEventArgs __) => count++;
        Localizer.Instance.PropertyChanged += handler;

        try
        {
            Localizer.Instance.Culture = new CultureInfo("en");
        }
        finally
        {
            Localizer.Instance.PropertyChanged -= handler;
        }

        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ko")]
    [InlineData("ja")]
    [InlineData("vi")]
    [InlineData("fil")]
    public void PickSupportedLanguage_PassesThroughExactSavedValue(string saved) => Assert.Equal(saved, Localizer.PickSupportedLanguage(saved));

    [Fact]
    public void PickSupportedLanguage_IsCaseInsensitive() => Assert.Equal("zh-Hans", Localizer.PickSupportedLanguage("ZH-hans"));

    [Theory]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hans")] // Only Simplified ships; Traditional folds to it.
    [InlineData("ja-JP", "ja")]
    [InlineData("ko-KR", "ko")]
    [InlineData("vi-VN", "vi")]
    [InlineData("fil-PH", "fil")]
    [InlineData("tl-PH", "fil")] // Legacy Tagalog locale folds to the Filipino satellite.
    [InlineData("en-US", "en")]
    [InlineData("fr-FR", "en")] // Unsupported → English baseline.
    [InlineData("de", "en")]
    public void PickSupportedLanguage_AutoDetectsFromOSCulture(string osTag, string expected) => Assert.Equal(expected, Localizer.PickSupportedLanguage(saved: null, osCulture: new CultureInfo(osTag)));

    [Fact]
    public void PickSupportedLanguage_BlankSaved_FallsThroughToOSCulture() => Assert.Equal("ko", Localizer.PickSupportedLanguage(saved: "   ", osCulture: new CultureInfo("ko-KR")));

    [Fact]
    public void PickSupportedLanguage_UnknownSaved_FallsThroughToOSCulture() =>
        // Saved value isn't in SupportedLanguages → treat like blank.
        Assert.Equal("ja", Localizer.PickSupportedLanguage(saved: "xx-YY", osCulture: new CultureInfo("ja-JP")));

    /// <summary>
    /// The "an update exists but this build cannot install it" line, in every language that ships.
    /// </summary>
    /// <remarks>
    /// Two ways this particular string fails without anything going red: a typo in the literal at
    /// the call site resolves to the key itself (see
    /// <see cref="Indexer_ReturnsKey_WhenKeyDoesNotExist"/>) and would be shown to the user verbatim;
    /// and a translation that loses <c>{0}</c> formats into a sentence announcing an update without
    /// saying which one. Neither is a compile error, and the inventory gates check that the key is
    /// PRESENT in all six files, not that what it holds is usable.
    /// </remarks>
    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    [InlineData("vi")]
    [InlineData("fil")]
    public void NotInstallableUpdateLine_ResolvesAndKeepsItsPlaceholder(string culture)
    {
        const string Key = "Main_CheckForUpdates_NotInstallable_Format";
        Localizer.Instance.Culture = new CultureInfo(culture);

        string value = Localizer.Instance[Key];

        Assert.NotEqual(Key, value);
        Assert.Contains("{0}", value, StringComparison.Ordinal);
        Assert.Contains("9.9.9", string.Format(CultureInfo.InvariantCulture, value, "9.9.9"), StringComparison.Ordinal);
    }
}
