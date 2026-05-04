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
    private readonly ProxyManager _proxyManager;
    private readonly IDialogService _dialogService;
    private readonly IAppLogger _logger;

    public ConnectionManagerViewModel(
        ProxySettingRepository repo,
        ProxyManager proxyManager,
        IDialogService dialogService,
        IAppLogger logger)
    {
        _repo = repo;
        _proxyManager = proxyManager;
        _dialogService = dialogService;
        _logger = logger;
    }

    public ObservableCollection<ProxySettingItem> Proxies { get; } = [];

    [ObservableProperty]
    private string saveStatus = string.Empty;

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
    private async Task TestAsync(ProxySettingItem? item)
    {
        if (item is null || item.IsTesting)
        {
            return;
        }

        await RunTestAsync(item);
    }

    [RelayCommand]
    private async Task TestAllAsync()
    {
        // Run tests in parallel — each test is independent and capped by its own
        // timeout. Skips rows already mid-test so a stuck test doesn't block this run.
        ProxySettingItem[] candidates = [.. Proxies.Where(p => !p.IsTesting)];
        await Task.WhenAll(candidates.Select(RunTestAsync));
    }

    private async Task RunTestAsync(ProxySettingItem item)
    {
        item.IsTesting = true;
        item.TestStatus = "Queued…";

        await _testSemaphore.WaitAsync().ConfigureAwait(true);
        try
        {
            item.TestStatus = "Testing…";
            ProxyTestResult result = await ProxyManager.TestProxyAsync(item.Dto).ConfigureAwait(true);
            item.TestStatus = result.Success
                ? string.IsNullOrEmpty(result.DetectedIp)
                    ? $"OK {result.LatencyMs}ms"
                    : $"OK {result.LatencyMs}ms ({result.DetectedIp})"
                : $"Failed: {result.Message}";
        }
        catch (Exception ex)
        {
            item.TestStatus = $"Failed: {ex.Message}";
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
    private async Task ImportAsync()
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

            SaveStatus = $"Imported {added} proxy(s) — click Save to persist";
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Import failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            FileName = $"csuploader-proxies-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
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
            string[] lines = [.. Proxies.Select(FormatProxyLine)];
            await File.WriteAllLinesAsync(dialog.FileName, lines);
            SaveStatus = $"Exported {lines.Length} proxy(s) to {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Export failed: {ex.Message}");
        }
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

        if (type == ProxyType.None || string.IsNullOrEmpty(uri.Host) || uri.Port <= 0)
        {
            return false;
        }

        dto.Type = type;
        dto.Host = uri.Host;
        dto.Port = uri.Port;
        dto.Enabled = true;

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            string[] parts = uri.UserInfo.Split(':', 2);
            dto.Username = Uri.UnescapeDataString(parts[0]);
            dto.Password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        }

        return true;
    }

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
