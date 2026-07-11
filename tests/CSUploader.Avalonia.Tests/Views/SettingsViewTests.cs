// <copyright file="SettingsViewTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using CSUploader.Views;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless verification of the ported <see cref="SettingsView"/> shell (Phase 6 Task 4): the ListBox
/// sidebar drives <see cref="SettingsViewModel.SelectedCategoryIndex"/> two-way and the four panels' IsVisible
/// follows it (port rule 39); the font combo lists <see cref="SettingsViewModel.GridFontFamilyOptions"/> and
/// two-way-binds <see cref="SettingsViewModel.GridFontFamily"/>; the value combos round-trip their selected
/// value through SelectedValueBinding (§Reality-check #11/#18, both the LocalizedOption and LanguageEntry
/// record shapes); a confirmation-prompt checkbox toggles its AskAgain; and the Database buttons carry the
/// Clear commands. Every shown window is closed in a <c>finally</c> (headless windows are process-global).
/// </summary>
public class SettingsViewTests
{
    // ── The 5 apply-immediately numeric fields must commit on LostFocus, not per-keystroke ──
    // Regression guard for a real bug: the UpdateSourceTrigger=LostFocus fix was first landed
    // MALFORMED (`{Binding X}, UpdateSourceTrigger=LostFocus}` — the trigger fell OUTSIDE the
    // binding braces, so the binding closed at the first `}` and the trigger was dead trailing
    // text → a silent per-keystroke no-op that still compiled and passed every other test).
    // These fields apply-and-persist immediately (no Save button); per-keystroke would flicker
    // the grid font mid-type and push transient values into live AppSettings + the DB.
    [Fact]
    public void NumericSettingsFields_CarryLostFocusTrigger_InsideTheBindingBraces()
    {
        string axaml = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoXaml.FindRepoRoot(), "src", "CSUploader.Avalonia", "Views", "SettingsView.axaml"));
        foreach (string prop in new[]
        {
            "GridFontSize", "MaxConcurrentUploadJobs", "MaxUploadsPerHost", "MaxConcurrentCPUJobs", "SpeedLimitValue",
        })
        {
            // Match from `{Binding <prop>` to the FIRST `}` — i.e. the binding's own braces. The
            // malformed form matches only `{Binding <prop>}` and fails the Contains; the correct
            // `{Binding <prop>, ..., UpdateSourceTrigger=LostFocus}` includes the trigger.
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                axaml, @"\{Binding " + prop + @"\b[^}]*\}");
            Assert.True(m.Success, $"no well-formed {{Binding {prop} ...}} found in SettingsView.axaml");
            Assert.Contains("UpdateSourceTrigger=LostFocus", m.Value, System.StringComparison.Ordinal);
        }
    }

    // Confirms the framework actually HONORS LostFocus on TextBox (so the fix above isn't a
    // framework-level no-op): a keystroke does NOT commit; losing focus does.
    [AvaloniaFact]
    public void Avalonia_HonorsLostFocusTrigger_OnTextBox_CommitOnlyAfterFocusLeaves()
    {
        LostFocusProbe probe = new();
        TextBox field = new()
        {
            [!TextBox.TextProperty] = new global::Avalonia.Data.Binding(nameof(LostFocusProbe.Value))
            {
                Mode = global::Avalonia.Data.BindingMode.TwoWay,
                UpdateSourceTrigger = global::Avalonia.Data.UpdateSourceTrigger.LostFocus,
            },
        };
        TextBox sink = new();
        Window win = new() { DataContext = probe, Content = new StackPanel { Children = { field, sink } } };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            field.Focus();
            Dispatcher.UIThread.RunJobs();
            field.Text = "typed";
            Dispatcher.UIThread.RunJobs();
            Assert.Null(probe.Value); // keystroke does NOT commit under LostFocus
            sink.Focus();             // move focus away → LostFocus fires
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("typed", probe.Value); // committed on focus loss
        }
        finally
        {
            win.Close();
        }
    }

    private sealed class LostFocusProbe : System.ComponentModel.INotifyPropertyChanged
    {
        private string? value;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string? Value
        {
            get => this.value;
            set
            {
                this.value = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Value)));
            }
        }
    }

    // ── Sidebar selection <-> SelectedCategoryIndex <-> panel visibility (port rule 39) ──

    [AvaloniaFact]
    public void Sidebar_And_CategoryIndex_SwitchTheVisiblePanel_BothDirections()
    {
        using VmHarness harness = new();
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            // Index 0 (default) → General panel live, the others hidden.
            Assert.Equal(0, view.Sidebar.SelectedIndex);
            Assert.True(view.GeneralPanel.IsVisible);
            Assert.False(view.UploadPanel.IsVisible);
            Assert.False(view.ConnectionPanel.IsVisible);
            Assert.False(view.AccountsPanel.IsVisible);

            // A sidebar click (SelectedIndex change) writes the VM index two-way and swaps to the Upload panel.
            view.Sidebar.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, harness.Vm.SelectedCategoryIndex);
            Assert.False(view.GeneralPanel.IsVisible);
            Assert.True(view.UploadPanel.IsVisible);

            // An external VM index change drives the sidebar selection and the panel back the other way.
            harness.Vm.SelectedCategoryIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, view.Sidebar.SelectedIndex);
            Assert.True(view.ConnectionPanel.IsVisible);
            Assert.False(view.UploadPanel.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Font combo lists the enumerated families and two-way-binds GridFontFamily ──

    [AvaloniaFact]
    public void FontCombo_ListsFontOptions_AndTwoWayBindsGridFontFamily()
    {
        using VmHarness harness = new(fontNames: ["Consolas", "Segoe UI", "Comic Sans MS"]);
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            Assert.Equal(3, view.FontCombo.ItemCount);

            // User picks a font → the VM's GridFontFamily follows (SelectedItem is two-way).
            view.FontCombo.SelectedItem = "Comic Sans MS";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Comic Sans MS", harness.Vm.GridFontFamily);

            // External VM change re-selects the matching item.
            harness.Vm.GridFontFamily = "Segoe UI";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Segoe UI", view.FontCombo.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Value combo (LocalizedOption<T> shape): SelectedValueBinding round-trips the enum value (§RC #11/#18) ──

    [AvaloniaFact]
    public void CloseActionCombo_LocalizedOptionShape_RoundTripsSelectedValue()
    {
        using VmHarness harness = new();
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            // Set the target SelectedValue → the two-way binding writes the enum into the VM.
            view.CloseActionCombo.SelectedValue = CloseAction.Exit;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(CloseAction.Exit, harness.Vm.CloseAction);

            // External VM change → the combo re-selects the matching LocalizedOption and reports the value.
            harness.Vm.CloseAction = CloseAction.MinimizeToTray;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(CloseAction.MinimizeToTray, view.CloseActionCombo.SelectedValue);
            var selected = Assert.IsType<LocalizedOption<CloseAction>>(view.CloseActionCombo.SelectedItem);
            Assert.Equal(CloseAction.MinimizeToTray, selected.Value);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Value combo (LanguageEntry shape): the second Value/Label record shape also round-trips (§RC #11/#18) ──

    [AvaloniaFact]
    public void LanguageCombo_LanguageEntryShape_RoundTripsSelectedValue()
    {
        // Setting Language flips Localizer.Instance.Culture (SettingsViewModel.OnLanguageChanged) — a
        // process-global mutation, so snapshot and restore it ([AvaloniaFact] discipline).
        CultureInfo originalCulture = Localizer.Instance.Culture;
        using VmHarness harness = new();
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            view.LanguageCombo.SelectedValue = "ko";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("ko", harness.Vm.Language);

            harness.Vm.Language = "ja";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("ja", view.LanguageCombo.SelectedValue);
            var selected = Assert.IsType<SettingsViewModel.LanguageEntry>(view.LanguageCombo.SelectedItem);
            Assert.Equal("ja", selected.Value);
        }
        finally
        {
            window.Close();
            Localizer.Instance.Culture = originalCulture;
        }
    }

    // ── Confirmation-prompt checkbox two-way-binds its item's AskAgain ──

    [AvaloniaFact]
    public void ConfirmationPromptCheckbox_TwoWayBindsAskAgain()
    {
        using VmHarness harness = new();
        SuppressedConfirmationItem item = new("SomePrompt", "Settings_General_ConfirmationPrompts_Title", askAgain: true);
        harness.Vm.ConfirmationPrompts.Add(item);

        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            // The one checkbox whose DataContext is the prompt item (the VM's own checkboxes bind the VM).
            CheckBox checkBox = view.GetVisualDescendants()
                .OfType<CheckBox>()
                .Single(cb => cb.DataContext is SuppressedConfirmationItem);
            Assert.True(checkBox.IsChecked);

            // Unticking the box clears the item's AskAgain (two-way).
            checkBox.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(item.AskAgain);

            // An external item change re-ticks the box.
            item.AskAgain = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(checkBox.IsChecked);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Database section: the two jd2 buttons carry the Clear commands ──

    [AvaloniaFact]
    public void DatabaseButtons_BoundToClearCommands()
    {
        using VmHarness harness = new();
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            // Scope to the Database section's own jd2 buttons by their commands — since Task 5 the Connection
            // panel adds its own jd2 buttons (realized-but-collapsed), so a global jd2 count is no longer 2.
            var clearButtons = view.GetVisualDescendants()
                .OfType<Button>()
                .Where(b => b.Classes.Contains("jd2"))
                .Where(b => ReferenceEquals(b.Command, harness.Vm.ClearDatabaseCommand)
                            || ReferenceEquals(b.Command, harness.Vm.ClearLogsCommand))
                .ToList();

            Assert.Equal(2, clearButtons.Count);
            Assert.Contains(clearButtons, b => ReferenceEquals(b.Command, harness.Vm.ClearDatabaseCommand));
            Assert.Contains(clearButtons, b => ReferenceEquals(b.Command, harness.Vm.ClearLogsCommand));
        }
        finally
        {
            window.Close();
        }
    }

    // ── helpers ──

    private static (Window Window, SettingsView View) Show(SettingsViewModel vm)
    {
        SettingsView view = new() { DataContext = vm };
        Window window = new() { Width = 1024, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    /// <summary>
    /// A real <see cref="SettingsViewModel"/> over an in-memory SQLite DB — the scratch-repo harness the WPF
    /// <c>SettingsViewModelTests</c> uses, plus a fake <see cref="IFontEnumerationService"/> so the font combo
    /// has items. LoadAsync is intentionally NOT called: the view tests drive the observable properties
    /// directly, so the DB stays empty (the auto-save partials fire-and-forget into the scratch repo).
    /// </summary>
    private sealed class VmHarness : IDisposable
    {
        private readonly SqliteConnection _connection;

        public VmHarness(IReadOnlyList<string>? fontNames = null)
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();

            DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
                .UseSqlite(_connection)
                .Options;

            TestDbContextFactory factory = new(options);
            using (CSUploaderDbContext db = factory.CreateDbContext())
            {
                db.Database.EnsureCreated();
            }

            SettingRepository settingRepo = new(factory);
            FileHosterLoginRepository loginRepo = new(factory);

            Vm = new SettingsViewModel(
                settingRepo,
                loginRepo,
                new AppSettings(),
                Mock.Of<IDialogService>(),
                Mock.Of<IAppLogger>(),
                Mock.Of<IAccountVerifier>(),
                fontEnumerationService: new FakeFontEnumerationService(fontNames ?? ["Consolas", "Segoe UI"]));
        }

        public SettingsViewModel Vm { get; }

        public void Dispose() => _connection.Dispose();

        private sealed class FakeFontEnumerationService(IReadOnlyList<string> names) : IFontEnumerationService
        {
            public IReadOnlyList<string> GetSystemFontFamilyNames() => names;
        }

        private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
            : IDbContextFactory<CSUploaderDbContext>
        {
            public CSUploaderDbContext CreateDbContext() => new(options);
        }
    }
}
