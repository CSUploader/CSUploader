// <copyright file="SettingsAccountsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Converters;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.ViewModels;
using CSUploader.Views;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless verification of the ported <see cref="SettingsView"/> Accounts panel (Phase 6 Task 6) — the
/// READ-ONLY accounts grid. Rule 32 is PER-GRID and THIS grid is the read-only side: the load-bearing tests
/// prove every data column binds OneWay (a TwoWay reflex with a ConvertBack-throwing converter would blank
/// the DTO), that the storage "Available" cell is the rule-34 MultiBinding-on-column (verdict: it WORKS on
/// 11.3.13 — no template-column fallback), that the Password cell is a fixed mask regardless of the DTO, that
/// the enable/disable checkbox fans out to the selection, that double-tap edits only on a row (not the
/// header), and the context-menu SelectedItems / row-vs-whitespace flip / Delete plumbing. Every window is
/// closed in a <c>finally</c> (headless windows are process-global).
/// </summary>
public class SettingsAccountsTests
{
    // ── Rule-32 read-only guard: every data column binds OneWay (the inverse of the proxy grid's rule-37) ──

    [AvaloniaFact]
    public void ReadOnlyTextColumns_AreOneWay()
    {
        using VmHarness harness = new();
        harness.Vm.AccountManager.Accounts.Add(Account("rapidgator.net", "user1"));
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            // The single-source text columns (Username / Type / Used / Added / Refreshed) must be OneWay —
            // Avalonia's DataGridTextColumn.Binding defaults to TwoWay and pushes ConvertBack on bind, which
            // with a throwing converter (ByteUnit / DateTimeFormat) would blank the cell/DTO. The columns are
            // x:DataType'd (dal:FileHosterLoginDto), so each is a CompiledBinding now; the Available
            // column's MultiBinding is filtered out by the type check exactly as it was before.
            var textBindings = view.accountsGrid.Columns
                .OfType<DataGridTextColumn>()
                .Select(c => c.Binding)
                .OfType<CompiledBinding>()
                .ToList();

            // Exactly the five single-source text columns (Username / Type / Used / Added / Refreshed) —
            // the Available column's MultiBinding is not a CompiledBinding and is asserted by its
            // own test. Pinning the count keeps a column that silently fell back to reflection from
            // passing as "one fewer compiled binding".
            Assert.Equal(5, textBindings.Count);
            Assert.All(textBindings, b => Assert.Equal(BindingMode.OneWay, b.Mode));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AvailableColumn_IsAMultiBindingOnTheColumn_OneWay()
    {
        using VmHarness harness = new();
        harness.Vm.AccountManager.Accounts.Add(Account("rapidgator.net", "user1"));
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            // Rule-34 verdict: the storage "Available" cell keeps its MultiBinding ON the DataGridTextColumn
            // (no template-column fallback needed on 11.3.13), and it is OneWay (read-only grid).
            MultiBinding multi = view.accountsGrid.Columns
                .OfType<DataGridTextColumn>()
                .Select(c => c.Binding)
                .OfType<MultiBinding>()
                .Single();

            Assert.Equal(BindingMode.OneWay, multi.Mode);
            Assert.IsType<StorageAvailableDisplayMultiConverter>(multi.Converter);
            Assert.Equal(3, multi.Bindings.Count); // used + quota + hoster name
        }
        finally
        {
            window.Close();
        }
    }

    // ── Rule-34: the MultiBinding cell renders the converter's output for known-cap / Unlimited / unknown ──

    [AvaloniaFact]
    public void AvailableCell_RendersMultiConverterOutput_AcrossStates()
    {
        const long gib = 1024L * 1024 * 1024;

        FileHosterLoginDto knownCap = Account("rapidgator.net", "known");
        knownCap.StorageUsedBytes = gib;         // 1 GiB used
        knownCap.StorageQuotaBytes = 10 * gib;   // of 10 GiB → 9 GiB available

        FileHosterLoginDto unlimited = Account("upstore.net", "unl");
        unlimited.StorageUsedBytes = 512L * 1024 * 1024; // used known, no quota → "Unlimited"

        FileHosterLoginDto unknown = Account("some.host", "unk"); // no storage info → "-"

        using VmHarness harness = new();
        harness.Vm.AccountManager.Accounts.Add(knownCap);
        harness.Vm.AccountManager.Accounts.Add(unlimited);
        harness.Vm.AccountManager.Accounts.Add(unknown);
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            // Each account's Available output is produced ONLY by the storage MultiBinding on the column
            // (remaining space "9 GiB" / "Unlimited" / "-"). Its presence in the realized row is the rule-34
            // proof that a MultiBinding realizes on a DataGridTextColumn.Binding (11.3.13).
            Assert.Contains(Expected(knownCap), RowTexts(view.accountsGrid, knownCap));
            Assert.Contains(Expected(unlimited), RowTexts(view.accountsGrid, unlimited));
            Assert.Contains(Expected(unknown), RowTexts(view.accountsGrid, unknown));

            // The known-cap row shows BOTH the used value AND a DIFFERENT remaining value — proof the
            // MultiBinding computed remaining space (quota − used), it didn't echo the Used column.
            string? usedText = new ByteUnitConverter().Convert(knownCap.StorageUsedBytes, typeof(string), "-", CultureInfo.InvariantCulture) as string;
            Assert.Contains(usedText, RowTexts(view.accountsGrid, knownCap));
            Assert.NotEqual(usedText, Expected(knownCap));
        }
        finally
        {
            window.Close();
        }
    }

    // ── Rule-32 behavioral: realizing the read-only grid does NOT blank the DTO (no ConvertBack write-back) ──

    [AvaloniaFact]
    public void RealizingGrid_DoesNotBlankTheDto()
    {
        FileHosterLoginDto account = Account("rapidgator.net", "keepme");
        account.StorageUsedBytes = 4096;
        account.StorageQuotaBytes = 8192;

        using VmHarness harness = new();
        harness.Vm.AccountManager.Accounts.Add(account);
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            Dispatcher.UIThread.RunJobs();

            // A OneWay bind never calls ConvertBack; a stray TwoWay would push default(long)/"" back and blank
            // these. The source survives intact.
            Assert.Equal("keepme", account.Username);
            Assert.Equal(4096, account.StorageUsedBytes);
            Assert.Equal(8192, account.StorageQuotaBytes);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Password is a fixed mask, never the DTO's cleartext secret ──

    [AvaloniaFact]
    public void PasswordCell_ShowsMask_RegardlessOfDto()
    {
        FileHosterLoginDto account = Account("rapidgator.net", "user1");
        account.Password = "hunter2-should-never-render";

        using VmHarness harness = new();
        harness.Vm.AccountManager.Accounts.Add(account);
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            var texts = RowFor(view.accountsGrid, account)
                .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

            Assert.Contains("******", texts);
            Assert.DoesNotContain(texts, t => t == account.Password);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Enable/disable checkbox fan-out: whole selection when the clicked row is selected, else just it ──

    [AvaloniaFact]
    public void EnableToggleFanOut_TargetsSelection_OrSingleRow()
    {
        FileHosterLoginDto a0 = Account("a.host", "a");
        FileHosterLoginDto a1 = Account("b.host", "b");
        FileHosterLoginDto a2 = Account("c.host", "c");

        using VmHarness harness = new();
        harness.Vm.AccountManager.Accounts.Add(a0);
        harness.Vm.AccountManager.Accounts.Add(a1);
        harness.Vm.AccountManager.Accounts.Add(a2);
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            view.accountsGrid.SelectedItems.Add(a0);
            view.accountsGrid.SelectedItems.Add(a1);
            Dispatcher.UIThread.RunJobs();

            // Clicking a checkbox INSIDE the selection fans the toggle out to every selected row.
            List<FileHosterLoginDto> inSelection = view.EnableToggleTargets(a0);
            Assert.Equal(2, inSelection.Count);
            Assert.Contains(a0, inSelection);
            Assert.Contains(a1, inSelection);

            // Clicking a checkbox OUTSIDE the selection targets that row alone.
            List<FileHosterLoginDto> outside = view.EnableToggleTargets(a2);
            Assert.Single(outside);
            Assert.Same(a2, outside[0]);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Double-tap edits on a row, not on the header (port rule 22) ──

    [AvaloniaFact]
    public void DoubleTap_EditsOnRow_NotOnHeader()
    {
        using VmHarness harness = new();
        harness.Vm.AccountManager.Accounts.Add(Account("rapidgator.net", "user1"));
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            DataGridRow row = view.accountsGrid.GetVisualDescendants().OfType<DataGridRow>().First();
            DataGridColumnHeader header = view.accountsGrid.GetVisualDescendants().OfType<DataGridColumnHeader>().First();

            Assert.True(SettingsView.SourceIsAccountRow(row));    // a row tap opens the editor
            Assert.False(SettingsView.SourceIsAccountRow(header)); // a header tap does not
        }
        finally
        {
            window.Close();
        }
    }

    // ── Disabled account rows dim to 0.45 (WPF DataGridRow DataTrigger → Opacity) ──

    [AvaloniaFact]
    public void DisabledAccountRow_DimsToPointFourFive_EnabledIsFull()
    {
        FileHosterLoginDto enabled = Account("a.host", "on");
        FileHosterLoginDto disabled = Account("b.host", "off");
        disabled.Disabled = true;

        using VmHarness harness = new();
        harness.Vm.AccountManager.Accounts.Add(enabled);
        harness.Vm.AccountManager.Accounts.Add(disabled);
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            Assert.Equal(1.0, RowFor(view.accountsGrid, enabled).Opacity, 3);
            Assert.Equal(0.45, RowFor(view.accountsGrid, disabled).Opacity, 3);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DisabledToOpacityConverter_MapsBoolToOpacity()
    {
        Assert.Equal(0.45, SettingsView.DisabledToOpacity.Convert(true, typeof(double), null, CultureInfo.InvariantCulture));
        Assert.Equal(1.0, SettingsView.DisabledToOpacity.Convert(false, typeof(double), null, CultureInfo.InvariantCulture));
    }

    // ── Rule 19: the SelectedItems-taking context items + the bottom Remove carry the grid's SelectedItems ──

    [AvaloniaFact]
    public void ContextMenuAndRemove_CarryTheGridsSelectedItems()
    {
        using VmHarness harness = new();
        harness.Vm.AccountManager.Accounts.Add(Account("rapidgator.net", "user1"));
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            Assert.Same(view.accountsGrid.SelectedItems, view.AccountsContextRefreshItem.CommandParameter);
            Assert.Same(view.accountsGrid.SelectedItems, view.AccountsContextEnableItem.CommandParameter);
            Assert.Same(view.accountsGrid.SelectedItems, view.AccountsContextDisableItem.CommandParameter);
            Assert.Same(view.accountsGrid.SelectedItems, view.AccountsContextDeleteItem.CommandParameter);
            Assert.Same(view.accountsGrid.SelectedItems, view.AccountsRemoveButton.CommandParameter);

            // Edit takes no parameter (operates on SelectedAccount).
            Assert.Null(view.AccountsContextEditItem.CommandParameter);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Rule 18: the row-only context items hide on a whitespace right-click, show on a row ──

    [AvaloniaFact]
    public void ContextMenu_RowOnlyItems_HiddenOnWhitespace_ShownOnRow()
    {
        FileHosterLoginDto account = Account("rapidgator.net", "user1");
        using VmHarness harness = new();
        harness.Vm.AccountManager.Accounts.Add(account);
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            view.ApplyAccountsRightClickTarget(RowFor(view.accountsGrid, account));
            Assert.True(view.ApplyAccountsContextRowItemVisibility());
            Assert.True(view.AccountsContextEditItem.IsVisible);
            Assert.True(view.AccountsContextRefreshItem.IsVisible);
            Assert.True(view.AccountsContextEnableItem.IsVisible);
            Assert.True(view.AccountsContextDisableItem.IsVisible);
            Assert.True(view.AccountsContextDeleteItem.IsVisible);

            view.ApplyAccountsRightClickTarget(view.accountsGrid);
            Assert.False(view.ApplyAccountsContextRowItemVisibility());
            Assert.False(view.AccountsContextEditItem.IsVisible);
            Assert.False(view.AccountsContextDeleteItem.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Rule 24: the Delete key removes the selection through RemoveSelectedAccountsCommand ──

    [AvaloniaFact]
    public void DeleteKey_WiredToRemoveSelectedAccounts_WithSelectedItems()
    {
        using VmHarness harness = new();
        harness.Vm.AccountManager.Accounts.Add(Account("rapidgator.net", "user1"));
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            KeyBinding binding = Assert.Single(view.accountsGrid.KeyBindings);
            Assert.Equal(Key.Delete, binding.Gesture.Key);
            Assert.Same(harness.Vm.AccountManager.RemoveSelectedAccountsCommand, binding.Command);
            Assert.Same(view.accountsGrid.SelectedItems, binding.CommandParameter);
        }
        finally
        {
            window.Close();
        }
    }

    // ── The Add / Remove / Refresh bottom-bar buttons bind to the VM commands ──

    [AvaloniaFact]
    public void BottomBarButtons_BoundToAccountCommands()
    {
        using VmHarness harness = new();
        harness.Vm.AccountManager.Accounts.Add(Account("rapidgator.net", "user1"));
        (Window window, SettingsView view) = Show(harness.Vm);
        try
        {
            var buttons = view.AccountsPanel.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Contains(buttons, b => ReferenceEquals(b.Command, harness.Vm.AccountManager.AddAccountDialogCommand));
            Assert.Contains(buttons, b => ReferenceEquals(b.Command, harness.Vm.AccountManager.RemoveSelectedAccountsCommand));
            Assert.Contains(buttons, b => ReferenceEquals(b.Command, harness.Vm.AccountManager.RefreshAllAccountsCommand));
        }
        finally
        {
            window.Close();
        }
    }

    // ── helpers ──

    private static string? Expected(FileHosterLoginDto a)
        => new StorageAvailableDisplayMultiConverter().Convert(
            new object?[] { a.StorageUsedBytes, a.StorageQuotaBytes, a.FileHosterName },
            typeof(string), null, CultureInfo.InvariantCulture) as string;

    // All realized text in a row (across every cell). The Available MultiBinding output is unique enough per
    // account (remaining bytes / "Unlimited" / "-") to assert by presence rather than by column position.
    private static IReadOnlyList<string?> RowTexts(DataGrid grid, FileHosterLoginDto item)
        => RowFor(grid, item).GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

    private static DataGridRow RowFor(DataGrid grid, FileHosterLoginDto item)
        => grid.GetVisualDescendants().OfType<DataGridRow>().First(r => ReferenceEquals(r.DataContext, item));

    private static FileHosterLoginDto Account(string hoster, string user)
        => new() { FileHosterName = hoster, Username = user, Password = "pw", AccountType = AccountType.Free };

    private static (Window Window, SettingsView View) Show(SettingsViewModel vm)
    {
        // Category 3 = the Accounts panel; it must be the visible panel or its grid never realizes. Wide
        // enough that every account column (~1250px) is in the horizontal viewport (the DataGrid virtualizes
        // columns).
        vm.SelectedCategoryIndex = 3;
        SettingsView view = new() { DataContext = vm };
        Window window = new() { Width = 1600, Height = 800, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    /// <summary>
    /// A real <see cref="SettingsViewModel"/> (with its <see cref="AccountManagerViewModel"/>) over an
    /// in-memory SQLite DB — the scratch-repo harness the shell tests use. LoadAsync is intentionally NOT
    /// called; the tests drive <c>Vm.AccountManager.Accounts</c> directly.
    /// </summary>
    private sealed class VmHarness : IDisposable
    {
        private readonly SqliteConnection _connection;

        public VmHarness()
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
                new AccountManagerViewModel(
                    loginRepo,
                    Mock.Of<IDialogService>(),
                    Mock.Of<IAppLogger>(),
                    Mock.Of<IAccountVerifier>()),
                new AppSettings(),
                Mock.Of<IDialogService>(),
                Mock.Of<IAppLogger>(),
                fontEnumerationService: new FakeFontEnumerationService(["Consolas", "Segoe UI"]));
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
