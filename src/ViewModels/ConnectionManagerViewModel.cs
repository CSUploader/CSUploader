// <copyright file="ConnectionManagerViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

/// <summary>
/// JD2-style Connection Manager. Lists configured proxies in priority order, exposes
/// per-row enabled/priority/type/host/etc., and supports add/remove, move up/down,
/// and import/export (plain text). Edits auto-persist via a debounced save that
/// reloads the live <see cref="ProxyManager"/> when changes settle.
/// </summary>
public partial class ConnectionManagerViewModel : ObservableObject
{
    /// <summary>
    /// Caps concurrent proxy connectivity tests. Without this, "Test All" on a long
    /// list fires 30+ DNS lookups + TLS handshakes simultaneously and stalls the UI
    /// while the dispatcher gets flooded with completion callbacks.
    /// </summary>
    private const int MaxConcurrentTests = 5;

    /// <summary>
    /// Quiet period after the last user edit before an auto-save fires. Long enough
    /// that typing in a Host cell doesn't hit the DB on every keystroke, short enough
    /// that closing the page right after a change still catches the persist.
    /// </summary>
    private const int AutoSaveDebounceMs = 750;

    /// <summary>
    /// Which row-level properties on a <see cref="ProxySettingItem"/> should trigger
    /// an auto-save when they change. Transient fields (TestStatus, TestOutcome,
    /// TestTransaction, IsTesting) are deliberately excluded.
    /// </summary>
    private static readonly HashSet<string> PersistedItemProperties = new(StringComparer.Ordinal)
    {
        nameof(ProxySettingItem.Enabled),
        nameof(ProxySettingItem.Type),
        nameof(ProxySettingItem.Host),
        nameof(ProxySettingItem.Port),
        nameof(ProxySettingItem.Username),
        nameof(ProxySettingItem.Password),
    };

    private readonly SemaphoreSlim _testSemaphore = new(MaxConcurrentTests, MaxConcurrentTests);
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private readonly ProxySettingRepository _repo;
    private readonly SettingRepository? _settingRepo;
    private readonly ProxyManager _proxyManager;
    private readonly IDialogService _dialogService;
    private readonly IAppLogger _logger;
    private readonly AppSettings? _appSettings;

    // Auto-save stays suppressed until LoadAsync has hydrated state from the DB,
    // otherwise the initial Add()/property-set bursts during construction or test
    // setup would each schedule a save against an unfinished VM.
    private bool _suppressAutoSave = true;
    private CancellationTokenSource? _autoSaveCts;

    public ConnectionManagerViewModel(
        ProxySettingRepository repo,
        ProxyManager proxyManager,
        IDialogService dialogService,
        IAppLogger logger,
        SettingRepository? settingRepo = null,
        AppSettings? appSettings = null)
    {
        _repo = repo;
        _settingRepo = settingRepo;
        _proxyManager = proxyManager;
        _dialogService = dialogService;
        _logger = logger;
        _appSettings = appSettings;

        // Live status updates: every upload that finishes through a proxy lands here.
        // Marshal to UI thread when WPF is running so we can safely mutate the
        // ObservableCollection / row VM.
        _proxyManager.ProxyResultObserved += OnProxyResultObserved;

        // Track structural changes on the proxy list so Add/Remove/Move all
        // auto-persist, and so we can attach/detach per-item PropertyChanged
        // handlers without leaking subscriptions.
        Proxies.CollectionChanged += OnProxiesCollectionChanged;
    }

    public ObservableCollection<ProxySettingItem> Proxies { get; } = [];

    [ObservableProperty]
    private string saveStatus = string.Empty;

    /// <summary>
    /// Bound to the "Automatically disable failing proxies" checkbox above the grid.
    /// Mirrored into <see cref="AppSettings.AutoDisableFailingProxies"/> on Save so
    /// background paths (PackageManager → ReportResult → us) see the user's choice.
    /// </summary>
    [ObservableProperty]
    private bool autoDisableFailingProxies = AppSettings.DefaultAutoDisableFailingProxies;

    /// <summary>
    /// Bound to the "Use proxies for uploads" checkbox above the grid. When false,
    /// uploads bypass every proxy regardless of per-row Enabled state — handy for
    /// adding/testing proxies before committing to using them. Mirrored into
    /// <see cref="AppSettings.ProxiesEnabled"/> on Save.
    /// </summary>
    [ObservableProperty]
    private bool proxiesEnabled = AppSettings.DefaultProxiesEnabled;

    /// <summary>
    /// Bound to the "Accept invalid server certificates" checkbox. When ticked, the
    /// upload pipeline's <see cref="System.Net.Http.HttpClient"/> accepts ANY server cert
    /// without validating the name or chain. Opt-in workaround for hosters whose storage
    /// CDN edges (e.g. FileBoom's <c>cmb-*.filestore.app</c>) ship certs that fail
    /// standard validation. Mirrored into <see cref="AppSettings.AllowInvalidServerCertificates"/>
    /// on Save. Disabled by default — turning it on disables MITM protection on every
    /// outbound request.
    /// </summary>
    [ObservableProperty]
    private bool allowInvalidServerCertificates = AppSettings.DefaultAllowInvalidServerCertificates;

    /// <summary>
    /// Drop-down options shown in the Type column.
    /// </summary>
    public static ProxyType[] ProxyTypeOptions { get; } =
        [ProxyType.None, ProxyType.Http, ProxyType.Https, ProxyType.Socks4, ProxyType.Socks5];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // Auto-save fires on every observable change once enabled; we don't want the
        // initial population of Proxies (or the checkbox hydration that follows) to
        // round-trip through the DB. Suppress for the whole hydration body and turn
        // it back on at the end so subsequent edits do persist.
        _suppressAutoSave = true;
        try
        {
            // CollectionChanged.Reset (raised by Clear) doesn't surface the removed
            // items, so detach handlers manually before clearing.
            foreach (ProxySettingItem item in Proxies)
            {
                item.PropertyChanged -= OnProxyItemPropertyChanged;
            }

            ProxySettingDto[] all = await _repo.GetAllAsync(cancellationToken);
            Proxies.Clear();
            foreach (ProxySettingDto dto in all.OrderBy(p => p.Priority).ThenBy(p => p.Id))
            {
                Proxies.Add(new ProxySettingItem(dto));
            }

            // SettingsViewModel.LoadAsync runs first and hydrates AppSettings; mirror
            // the current values into our bound properties so the checkboxes reflect
            // the saved choices.
            if (_appSettings is not null)
            {
                AutoDisableFailingProxies = _appSettings.AutoDisableFailingProxies;
                ProxiesEnabled = _appSettings.ProxiesEnabled;
                AllowInvalidServerCertificates = _appSettings.AllowInvalidServerCertificates;
            }
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    private void OnProxyResultObserved(object? sender, ProxyResultEventArgs e)
    {
        void Apply()
        {
            ProxySettingItem? item = Proxies.FirstOrDefault(p => p.Dto.Id == e.ProxyId);
            if (item is null)
            {
                return;
            }

            if (e.Success)
            {
                item.TestStatus = Localizer.Instance["Settings_Conn_Status_OkLive"];
                item.TestOutcome = ProxyTestOutcome.Ok;
            }
            else
            {
                string firstLine = (e.Message ?? "upload failed").Split('\n')[0];
                if (firstLine.Length > 120)
                {
                    firstLine = firstLine[..120] + "…";
                }

                item.TestStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings_Conn_Status_Failed_Format"], firstLine);
                item.TestOutcome = ProxyTestOutcome.Failed;

                if (_appSettings?.AutoDisableFailingProxies ?? AutoDisableFailingProxies)
                {
                    item.Enabled = false;
                }
            }
        }

        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            dispatcher.BeginInvoke(Apply);
        }
    }

    [RelayCommand]
    private void Add()
    {
        // Seed an empty DTO with sensible defaults and let the user fill it in via the
        // modal editor. The grid row is only created when the dialog returns Save —
        // cancelling no-ops, which is friendlier than the prior "add an empty row and
        // hope the user knows to tab through every column" flow.
        ProxySettingDto seed = new()
        {
            Type = ProxyType.Http,
            Host = string.Empty,
            Port = 8080,
            Enabled = true,
            Priority = Proxies.Count,
        };

        ProxySettingDto? edited = _dialogService.ShowEditProxyDialog(seed);
        if (edited is null)
        {
            return;
        }

        edited.Priority = Proxies.Count;
        Proxies.Add(new ProxySettingItem(edited));
    }

    [RelayCommand]
    private void RemoveSelected(IList? selectedItems)
    {
        if (selectedItems is null || selectedItems.Count == 0)
        {
            return;
        }

        ProxySettingItem[] items = [.. selectedItems.OfType<ProxySettingItem>()];
        if (items.Length == 0)
        {
            return;
        }

        if (!_dialogService.ShowOptOutConfirmation(
                ConfirmationKeys.RemoveProxy,
                items.Length == 1
                    ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings_Conn_RemoveProxy_One_Format"], items[0].Host, items[0].Port)
                    : string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings_Conn_RemoveProxy_Many_Format"], items.Length),
                Localizer.Instance["Settings_Conn_RemoveProxy_Title"]))
        {
            return;
        }

        foreach (ProxySettingItem item in items)
        {
            Proxies.Remove(item);
        }
    }

    [RelayCommand]
    private void RemoveFailed()
    {
        ProxySettingItem[] failing = [.. Proxies.Where(p => p.TestOutcome == ProxyTestOutcome.Failed)];
        if (failing.Length == 0)
        {
            return;
        }

        if (!_dialogService.ShowOptOutConfirmation(
                ConfirmationKeys.RemoveProxy,
                failing.Length == 1
                    ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings_Conn_RemoveFailedProxy_One_Format"], failing[0].Host, failing[0].Port)
                    : string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings_Conn_RemoveFailedProxy_Many_Format"], failing.Length),
                Localizer.Instance["Settings_Conn_RemoveFailedProxy_Title"]))
        {
            return;
        }

        foreach (ProxySettingItem item in failing)
        {
            Proxies.Remove(item);
        }
    }

    [RelayCommand]
    private void MoveUp(ProxySettingItem? item)
    {
        if (item is null)
        {
            return;
        }

        int index = Proxies.IndexOf(item);
        if (index <= 0)
        {
            return;
        }

        Proxies.Move(index, index - 1);
    }

    [RelayCommand]
    private void MoveDown(ProxySettingItem? item)
    {
        if (item is null)
        {
            return;
        }

        int index = Proxies.IndexOf(item);
        if (index < 0 || index >= Proxies.Count - 1)
        {
            return;
        }

        Proxies.Move(index, index + 1);
    }

    [RelayCommand]
    private async Task TestAsync(object? parameter)
    {
        // Accept either the legacy single-item parameter or a collection from
        // DataGrid.SelectedItems so right-click → Test on a multi-select tests every
        // selected row, not just the one under the cursor.
        ProxySettingItem[] candidates = parameter switch
        {
            ProxySettingItem single => [single],
            IList list => [.. list.OfType<ProxySettingItem>()],
            _ => [],
        };

        candidates = [.. candidates.Where(p => !p.IsTesting)];
        if (candidates.Length == 0)
        {
            return;
        }

        await Task.WhenAll(candidates.Select(RunTestAsync));
    }

    [RelayCommand]
    private async Task TestAllAsync()
    {
        // Run tests in parallel — each test is independent and capped by its own
        // timeout. Skips rows already mid-test so a stuck test doesn't block this run.
        ProxySettingItem[] candidates = [.. Proxies.Where(p => !p.IsTesting)];
        await Task.WhenAll(candidates.Select(RunTestAsync));
    }

    [RelayCommand]
    private static void ShowTestDetails(ProxySettingItem? item)
    {
        if (item?.TestTransaction is null)
        {
            return;
        }

        // Reuses the Logs tab's request/response viewer so proxy tests get the same
        // headers/body/hex tabs as upload traffic — no separate UI to maintain.
        Views.HttpDetailsWindow window = new(item.TestTransaction)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        window.ShowDialog();
    }

    private async Task RunTestAsync(ProxySettingItem item)
    {
        item.IsTesting = true;
        item.TestStatus = Localizer.Instance["Settings_Conn_Status_Queued"];
        item.TestOutcome = ProxyTestOutcome.Untested;
        item.TestTransaction = null;

        await _testSemaphore.WaitAsync().ConfigureAwait(true);
        try
        {
            item.TestStatus = Localizer.Instance["Settings_Conn_Status_Testing"];
            bool acceptInvalidCerts = _appSettings?.AllowInvalidServerCertificates ?? AllowInvalidServerCertificates;
            ProxyTestResult result = await ProxyManager.TestProxyAsync(item.Dto, _logger, acceptInvalidCertificates: acceptInvalidCerts).ConfigureAwait(true);

            // Status line stays compact — full request+response lives in TestTransaction
            // behind the Details button so multi-KB error pages from misbehaving proxies
            // don't explode the grid row.
            if (result.Success)
            {
                item.TestStatus = string.IsNullOrEmpty(result.DetectedIp)
                    ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings_Conn_Status_OkLatencyUnknown_Format"], result.LatencyMs)
                    : string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings_Conn_Status_OkLatencyIp_Format"], result.LatencyMs, result.DetectedIp);
                item.TestOutcome = ProxyTestOutcome.Ok;
            }
            else
            {
                string firstLine = (result.Message ?? string.Empty).Split('\n')[0];
                if (firstLine.Length > 120)
                {
                    firstLine = firstLine[..120] + "…";
                }

                item.TestStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings_Conn_Status_Failed_Format"], firstLine);
                item.TestOutcome = ProxyTestOutcome.Failed;

                // Honour the per-VM toggle (mirrors AppSettings on Save) so the user can
                // opt out of the auto-uncheck while still seeing the red status icon.
                if (AutoDisableFailingProxies)
                {
                    item.Enabled = false;
                }
            }

            item.TestTransaction = result.Transaction;
        }
        catch (Exception ex)
        {
            item.TestStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings_Conn_Status_Failed_Format"], ex.Message);
            item.TestOutcome = ProxyTestOutcome.Failed;
            item.TestTransaction = null;
        }
        finally
        {
            _testSemaphore.Release();
            item.IsTesting = false;
        }
    }

    /// <summary>
    /// Explicit save entry point retained for tests and for any future callers that
    /// need a synchronous persist. Cancels any pending debounced auto-save so the two
    /// paths don't double up, then runs the same persist body the auto-save uses.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        _autoSaveCts?.Cancel();
        await PersistAsync(cancellationToken);
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ProxySettingDto[] existing = await _repo.GetAllAsync(cancellationToken);
            HashSet<int> keepIds = [];

            // Snapshot so a concurrent mutation (e.g. a ProxyResultObserved callback
            // disabling a row mid-save) doesn't shift indices under us. Any change
            // missed here just re-triggers its own auto-save.
            ProxySettingItem[] snapshot = [.. Proxies];
            for (int i = 0; i < snapshot.Length; i++)
            {
                ProxySettingItem item = snapshot[i];
                item.Dto.Priority = i;

                if (item.Dto.Id == 0)
                {
                    await _repo.InsertAsync(item.Dto, cancellationToken);
                }
                else
                {
                    await _repo.UpdateAsync(item.Dto, cancellationToken);
                }

                keepIds.Add(item.Dto.Id);
            }

            int[] removed = [.. existing.Select(e => e.Id).Where(id => !keepIds.Contains(id))];
            if (removed.Length > 0)
            {
                await _repo.DeleteAsync(removed, cancellationToken);
            }

            // Persist the page-level toggles alongside the proxy list so they auto-save
            // together with the grid edits.
            if (_appSettings is not null)
            {
                _appSettings.AutoDisableFailingProxies = AutoDisableFailingProxies;
                _appSettings.ProxiesEnabled = ProxiesEnabled;
                _appSettings.AllowInvalidServerCertificates = AllowInvalidServerCertificates;
            }

            if (_settingRepo is not null)
            {
                await UpsertSettingAsync(SettingKey.AutoDisableFailingProxies, AutoDisableFailingProxies ? "true" : "false", cancellationToken);
                await UpsertSettingAsync(SettingKey.ProxiesEnabled, ProxiesEnabled ? "true" : "false", cancellationToken);
                await UpsertSettingAsync(SettingKey.AllowInvalidServerCertificates, AllowInvalidServerCertificates ? "true" : "false", cancellationToken);
            }

            await _proxyManager.ReloadAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Failed to save proxies: {ex.Message}");
            SaveStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings_Conn_Status_SaveFailed_Format"], ex.Message);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // Auto-save hooks. Every edit that should round-trip to the DB ultimately calls
    // ScheduleAutoSave, which debounces so a burst of changes (typing, paste-import,
    // multi-row remove) collapses into one persist.

    private void OnProxiesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (object obj in e.NewItems)
            {
                if (obj is ProxySettingItem item)
                {
                    item.PropertyChanged += OnProxyItemPropertyChanged;
                }
            }
        }

        if (e.OldItems is not null)
        {
            foreach (object obj in e.OldItems)
            {
                if (obj is ProxySettingItem item)
                {
                    item.PropertyChanged -= OnProxyItemPropertyChanged;
                }
            }
        }

        ScheduleAutoSave();
    }

    private void OnProxyItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && PersistedItemProperties.Contains(e.PropertyName))
        {
            ScheduleAutoSave();
        }
    }

    partial void OnAutoDisableFailingProxiesChanged(bool value) => ScheduleAutoSave();

    partial void OnProxiesEnabledChanged(bool value) => ScheduleAutoSave();

    partial void OnAllowInvalidServerCertificatesChanged(bool value) => ScheduleAutoSave();

    private void ScheduleAutoSave()
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _autoSaveCts?.Cancel();
        CancellationTokenSource cts = new();
        _autoSaveCts = cts;
        _ = AutoSaveAfterDelayAsync(cts.Token);
    }

    private async Task AutoSaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AutoSaveDebounceMs, cancellationToken).ConfigureAwait(true);
            await PersistAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Auto-save failed: {ex.Message}");
        }
    }

    private async Task UpsertSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        if (_settingRepo is null)
        {
            return;
        }

        SettingDto? existing = await _settingRepo.FindByKeyAsync(key, cancellationToken);
        if (existing is null)
        {
            await _settingRepo.InsertAsync(new SettingDto { Key = key, Value = value }, cancellationToken);
        }
        else
        {
            existing.Value = value;
            await _settingRepo.UpdateAsync(existing, cancellationToken);
        }
    }

    [RelayCommand]
    private async Task ImportFromFile()
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Filter = Localizer.Instance["Settings_Conn_ImportProxies_FileFilter"],
            DefaultExt = ".txt",
            Title = Localizer.Instance["Settings_Conn_ImportProxies_FileDialogTitle"],
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            string[] lines = await File.ReadAllLinesAsync(dialog.FileName);
            int added = AppendFromLines(lines);
            SaveStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings_Conn_Status_Imported_Format"], added);
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Import failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ImportFromText()
    {
        Views.ProxyTextDialog dialog = new(
            Localizer.Instance["Settings_Conn_ImportProxies_DialogTitle"],
            Localizer.Instance["Settings_Conn_ImportProxies_DialogDesc"],
            initialText: string.Empty,
            readOnly: false)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string[] lines = (dialog.ResultText ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        int added = AppendFromLines(lines);
        SaveStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings_Conn_Status_Imported_Format"], added);
    }

    [RelayCommand]
    private async Task ExportAllToFile()
        => await ExportToFileAsync(Proxies, ProxyExportKind.All);

    [RelayCommand]
    private async Task ExportOkToFile()
        => await ExportToFileAsync(Proxies.Where(p => p.TestOutcome == ProxyTestOutcome.Ok), ProxyExportKind.Ok);

    [RelayCommand]
    private async Task ExportSelectedToFile(IList? selectedItems)
    {
        ProxySettingItem[] items = SelectedProxies(selectedItems);
        if (items.Length == 0)
        {
            return;
        }

        await ExportToFileAsync(items, ProxyExportKind.Selected);
    }

    [RelayCommand]
    private void ExportAllToText()
        => ShowExportDialog(Proxies, ProxyExportKind.All);

    [RelayCommand]
    private void ExportOkToText()
        => ShowExportDialog(Proxies.Where(p => p.TestOutcome == ProxyTestOutcome.Ok), ProxyExportKind.Ok);

    // CA1822 disabled: [RelayCommand] requires an instance method so the generator can
    // expose ExportSelectedToTextCommand as an instance property.
#pragma warning disable CA1822
    [RelayCommand]
    private void ExportSelectedToText(IList? selectedItems)
    {
        ProxySettingItem[] items = SelectedProxies(selectedItems);
        if (items.Length == 0)
        {
            return;
        }

        ShowExportDialog(items, ProxyExportKind.Selected);
    }
#pragma warning restore CA1822

    private static ProxySettingItem[] SelectedProxies(IList? selectedItems)
        => selectedItems?.OfType<ProxySettingItem>().ToArray() ?? [];

    private enum ProxyExportKind
    {
        All,
        Ok,
        Selected,
    }

    private int AppendFromLines(IEnumerable<string> lines)
    {
        int added = 0;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (TryParseProxyLine(line, out ProxySettingDto? dto))
            {
                dto.Priority = Proxies.Count;
                Proxies.Add(new ProxySettingItem(dto));
                added++;
            }
            else
            {
                _logger.Log(this, LogType.Error, $"Could not parse proxy line: {line}");
            }
        }

        return added;
    }

    private static IEnumerable<string> BuildExportLines(IEnumerable<ProxySettingItem> items) =>
        items.Select(FormatProxyLine);

    private async Task ExportToFileAsync(IEnumerable<ProxySettingItem> items, ProxyExportKind kind)
    {
        string suffix = kind switch
        {
            ProxyExportKind.Ok => "-ok",
            ProxyExportKind.Selected => "-selected",
            _ => string.Empty,
        };
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            FileName = $"csuploader-proxies{suffix}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = Localizer.Instance["Settings_Conn_ImportProxies_FileFilter"],
            DefaultExt = ".txt",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            string[] lines = [.. BuildExportLines(items)];
            await File.WriteAllLinesAsync(dialog.FileName, lines);
            SaveStatus = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Settings_Conn_Status_ExportedToFile_Format"], lines.Length, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Export failed: {ex.Message}");
        }
    }

    private static void ShowExportDialog(IEnumerable<ProxySettingItem> items, ProxyExportKind kind)
    {
        string[] lines = [.. BuildExportLines(items)];
        string text = string.Join(Environment.NewLine, lines);
        (string titleKey, string descKey) = kind switch
        {
            ProxyExportKind.Ok => ("Settings_Conn_ExportOk_DialogTitle", "Settings_Conn_ExportOk_Desc_Format"),
            ProxyExportKind.Selected => ("Settings_Conn_ExportSelected_DialogTitle", "Settings_Conn_ExportSelected_Desc_Format"),
            _ => ("Settings_Conn_ExportAll_DialogTitle", "Settings_Conn_ExportAll_Desc_Format"),
        };
        Views.ProxyTextDialog dialog = new(
            Localizer.Instance[titleKey],
            string.Format(CultureInfo.CurrentCulture, Localizer.Instance[descKey], lines.Length),
            text,
            readOnly: true)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Parses a single proxy line: <c>scheme://[user:pass@]host:port</c>.
    /// Examples:
    ///   http://1.2.3.4:8080
    ///   socks5://user:pass@1.2.3.4:1080
    ///   https://example.com:443
    /// </summary>
    internal static bool TryParseProxyLine(string line, out ProxySettingDto dto)
    {
        dto = new ProxySettingDto();
        if (!Uri.TryCreate(line, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        ProxyType type = uri.Scheme.ToLowerInvariant() switch
        {
            "http" => ProxyType.Http,
            "https" => ProxyType.Https,
            "socks4" => ProxyType.Socks4,
            "socks5" => ProxyType.Socks5,
            _ => ProxyType.None,
        };

        if (type == ProxyType.None || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        // Scheme-default ports for entries that omit one. Uri.Port already supplies 80/443
        // for http/https, but socks4/socks5 are unknown to System.Uri (Port = -1).
        int port = uri.Port > 0 ? uri.Port : DefaultPortFor(type);
        if (port <= 0)
        {
            return false;
        }

        dto.Type = type;
        dto.Host = uri.Host;
        dto.Port = port;
        dto.Enabled = true;

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            string[] parts = uri.UserInfo.Split(':', 2);
            dto.Username = Uri.UnescapeDataString(parts[0]);
            dto.Password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        }

        return true;
    }

    private static int DefaultPortFor(ProxyType type) => type switch
    {
        ProxyType.Http => 80,
        ProxyType.Https => 443,
        ProxyType.Socks4 or ProxyType.Socks5 => 1080,
        _ => 0,
    };

    internal static string FormatProxyLine(ProxySettingItem item)
    {
        string scheme = item.Dto.Type switch
        {
            ProxyType.Http => "http",
            ProxyType.Https => "https",
            ProxyType.Socks4 => "socks4",
            ProxyType.Socks5 => "socks5",
            _ => "http",
        };

        string credentials = !string.IsNullOrEmpty(item.Dto.Username)
            ? $"{Uri.EscapeDataString(item.Dto.Username)}:{Uri.EscapeDataString(item.Dto.Password ?? string.Empty)}@"
            : string.Empty;

        return string.Create(CultureInfo.InvariantCulture, $"{scheme}://{credentials}{item.Dto.Host}:{item.Dto.Port}");
    }
}
