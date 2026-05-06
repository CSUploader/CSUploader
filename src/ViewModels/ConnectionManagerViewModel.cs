// <copyright file="ConnectionManagerViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

/// <summary>
/// JD2-style Connection Manager. Lists configured proxies in priority order, exposes
/// per-row enabled/priority/type/host/etc., and supports add/remove, move up/down,
/// import/export (plain text), and save (which reloads the live <see cref="ProxyManager"/>).
/// </summary>
public partial class ConnectionManagerViewModel : ObservableObject
{
    /// <summary>
    /// Caps concurrent proxy connectivity tests. Without this, "Test All" on a long
    /// list fires 30+ DNS lookups + TLS handshakes simultaneously and stalls the UI
    /// while the dispatcher gets flooded with completion callbacks.
    /// </summary>
    private const int MaxConcurrentTests = 5;

    private readonly SemaphoreSlim _testSemaphore = new(MaxConcurrentTests, MaxConcurrentTests);

    private readonly ProxySettingRepository _repo;
    private readonly SettingRepository? _settingRepo;
    private readonly ProxyManager _proxyManager;
    private readonly IDialogService _dialogService;
    private readonly IAppLogger _logger;
    private readonly AppSettings? _appSettings;

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
    /// Drop-down options shown in the Type column.
    /// </summary>
    public static ProxyType[] ProxyTypeOptions { get; } =
        [ProxyType.None, ProxyType.Http, ProxyType.Https, ProxyType.Socks4, ProxyType.Socks5];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ProxySettingDto[] all = await _repo.GetAllAsync(cancellationToken);
        Proxies.Clear();
        foreach (ProxySettingDto dto in all.OrderBy(p => p.Priority).ThenBy(p => p.Id))
        {
            Proxies.Add(new ProxySettingItem(dto));
        }

        // SettingsViewModel.LoadAsync runs first and hydrates AppSettings; mirror the
        // current value into our bound property so the checkbox reflects the saved choice.
        if (_appSettings is not null)
        {
            AutoDisableFailingProxies = _appSettings.AutoDisableFailingProxies;
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
                item.TestStatus = "OK (live)";
            }
            else
            {
                string firstLine = (e.Message ?? "upload failed").Split('\n')[0];
                if (firstLine.Length > 120)
                {
                    firstLine = firstLine[..120] + "…";
                }

                item.TestStatus = $"Failed: {firstLine}";

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
        ProxySettingDto dto = new()
        {
            Type = ProxyType.Http,
            Host = string.Empty,
            Port = 8080,
            Enabled = true,
            Priority = Proxies.Count,
        };
        Proxies.Add(new ProxySettingItem(dto));
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
                    ? $"Remove proxy '{items[0].Host}:{items[0].Port}'?"
                    : $"Remove {items.Length} proxies?",
                "Remove proxy"))
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
                    ? $"Remove the failed proxy '{failing[0].Host}:{failing[0].Port}'?"
                    : $"Remove {failing.Length} failed proxies?",
                "Remove failed proxies"))
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
        item.TestStatus = "Queued…";
        item.TestTransaction = null;

        await _testSemaphore.WaitAsync().ConfigureAwait(true);
        try
        {
            item.TestStatus = "Testing…";
            ProxyTestResult result = await ProxyManager.TestProxyAsync(item.Dto, _logger).ConfigureAwait(true);

            // Status line stays compact — full request+response lives in TestTransaction
            // behind the Details button so multi-KB error pages from misbehaving proxies
            // don't explode the grid row.
            if (result.Success)
            {
                item.TestStatus = string.IsNullOrEmpty(result.DetectedIp)
                    ? $"OK {result.LatencyMs}ms (unexpected response)"
                    : $"OK {result.LatencyMs}ms ({result.DetectedIp})";
            }
            else
            {
                string firstLine = (result.Message ?? string.Empty).Split('\n')[0];
                if (firstLine.Length > 120)
                {
                    firstLine = firstLine[..120] + "…";
                }

                item.TestStatus = $"Failed: {firstLine}";

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
            item.TestStatus = $"Failed: {ex.Message}";
            item.TestTransaction = null;
        }
        finally
        {
            _testSemaphore.Release();
            item.IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ProxySettingDto[] existing = await _repo.GetAllAsync(cancellationToken);
            HashSet<int> keepIds = [];

            // Re-number Priority based on current order
            for (int i = 0; i < Proxies.Count; i++)
            {
                ProxySettingItem item = Proxies[i];
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

            // Persist the auto-disable preference alongside the proxy list so toggling
            // the checkbox + clicking Save is the single committal action on this page.
            if (_appSettings is not null)
            {
                _appSettings.AutoDisableFailingProxies = AutoDisableFailingProxies;
            }

            if (_settingRepo is not null)
            {
                string value = AutoDisableFailingProxies ? "true" : "false";
                SettingDto? existingSetting = await _settingRepo.FindByKeyAsync(SettingKey.AutoDisableFailingProxies, cancellationToken);
                if (existingSetting is null)
                {
                    await _settingRepo.InsertAsync(new SettingDto { Key = SettingKey.AutoDisableFailingProxies, Value = value }, cancellationToken);
                }
                else
                {
                    existingSetting.Value = value;
                    await _settingRepo.UpdateAsync(existingSetting, cancellationToken);
                }
            }

            await _proxyManager.ReloadAsync(cancellationToken);

            SaveStatus = "Saved";
            try
            {
                await Task.Delay(1500, cancellationToken);
            }
            catch (TaskCanceledException) { }

            SaveStatus = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Failed to save proxies: {ex.Message}");
            SaveStatus = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportFromFile()
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Filter = "Proxy lists (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
            Title = "Import proxies",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            string[] lines = await File.ReadAllLinesAsync(dialog.FileName);
            int added = AppendFromLines(lines);
            SaveStatus = $"Imported {added} proxy(s) — click Save to persist";
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
            "Import Proxies",
            "Paste proxy lines (one per line). Format: scheme://[user:pass@]host[:port] — port defaults to 80/443/1080 by scheme.",
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
        SaveStatus = $"Imported {added} proxy(s) — click Save to persist";
    }

    [RelayCommand]
    private async Task ExportAllToFile() => await ExportToFileAsync(okOnly: false);

    [RelayCommand]
    private async Task ExportOkToFile() => await ExportToFileAsync(okOnly: true);

    [RelayCommand]
    private void ExportAllToText() => ShowExportDialog(okOnly: false);

    [RelayCommand]
    private void ExportOkToText() => ShowExportDialog(okOnly: true);

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

    private IEnumerable<string> BuildExportLines(bool okOnly) =>
        Proxies
            .Where(p => !okOnly || p.TestOutcome == ProxyTestOutcome.Ok)
            .Select(FormatProxyLine);

    private async Task ExportToFileAsync(bool okOnly)
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            FileName = okOnly
                ? $"csuploader-proxies-ok-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
                : $"csuploader-proxies-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Proxy lists (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            string[] lines = [.. BuildExportLines(okOnly)];
            await File.WriteAllLinesAsync(dialog.FileName, lines);
            SaveStatus = $"Exported {lines.Length} proxy(s) to {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Export failed: {ex.Message}");
        }
    }

    private void ShowExportDialog(bool okOnly)
    {
        string text = string.Join(Environment.NewLine, BuildExportLines(okOnly));
        int count = text.Length == 0 ? 0 : text.Split('\n').Length;
        Views.ProxyTextDialog dialog = new(
            okOnly ? "Export Tested-OK Proxies" : "Export All Proxies",
            okOnly
                ? $"{count} proxy(s) with a successful last test:"
                : $"{count} proxy(s):",
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
