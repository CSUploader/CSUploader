// <copyright file="SettingsViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingRepository _settingRepository;
    private readonly FileHosterLoginRepository _accountRepository;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogService;
    private readonly IAppLogger _logger;

    // ── General settings ──

    [ObservableProperty]
    private string tempArchiveDirectory = AppSettings.DefaultTempArchiveDirectory;

    // ── Upload settings ──

    [ObservableProperty]
    private int maxConcurrentCPUJobs = AppSettings.DefaultMaxConcurrentCPUJobs;

    [ObservableProperty]
    private int maxConcurrentUploadJobs = AppSettings.DefaultMaxConcurrentUploadJobs;

    [ObservableProperty]
    private bool speedLimitEnabled;

    [ObservableProperty]
    private int speedLimitValue;

    // ── Navigation ──

    [ObservableProperty]
    private int selectedCategoryIndex;

    // ── Account management ──

    [ObservableProperty]
    private FileHosterLoginDto? selectedAccount;

    [ObservableProperty]
    private string newAccountHoster = string.Empty;

    [ObservableProperty]
    private string newAccountUsername = string.Empty;

    [ObservableProperty]
    private string newAccountPassword = string.Empty;

    [ObservableProperty]
    private AccountType newAccountType = AccountType.Free;

    [ObservableProperty]
    private string checkAccountStatus = string.Empty;

    [ObservableProperty]
    private bool isCheckingAccount;

    public SettingsViewModel(
        SettingRepository settingRepository,
        FileHosterLoginRepository accountRepository,
        AppSettings settings,
        IDialogService dialogService,
        IAppLogger logger)
    {
        _settingRepository = settingRepository;
        _accountRepository = accountRepository;
        _settings = settings;
        _dialogService = dialogService;
        _logger = logger;
    }

    public ObservableCollection<FileHosterLoginDto> Accounts { get; } = [];

    public static string[] AvailableHosters => [.. FileHosterClient.FileHosters.Keys];

#pragma warning disable CA1822
    public AccountType[] AccountTypes => [AccountType.Free, AccountType.Premium];
#pragma warning restore CA1822

    // ── Load ──

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // Load settings
        SettingDto[] settings = await _settingRepository.GetAllAsync(cancellationToken);

        foreach (SettingDto setting in settings)
        {
            switch (setting.Key)
            {
                case var k when k == SettingKey.MaxConcurrentCPUJobs:
                    if (int.TryParse(setting.Value, out int cpuJobs))
                    {
                        MaxConcurrentCPUJobs = cpuJobs;
                    }

                    break;

                case var k when k == SettingKey.MaxConcurrentUploadJobs:
                    if (int.TryParse(setting.Value, out int uploadJobs))
                    {
                        MaxConcurrentUploadJobs = uploadJobs;
                    }

                    break;

                case var k when k == SettingKey.SpeedLimit:
                    if (int.TryParse(setting.Value, out int speedLimit))
                    {
                        SpeedLimitValue = speedLimit;
                        SpeedLimitEnabled = speedLimit > 0;
                    }

                    break;

                case var k when k == SettingKey.TempArchiveDirectory:
                    if (!string.IsNullOrWhiteSpace(setting.Value))
                    {
                        TempArchiveDirectory = setting.Value;
                    }

                    break;
            }
        }

        _settings.MaxConcurrentCPUJobs = MaxConcurrentCPUJobs;
        _settings.MaxConcurrentUploadJobs = MaxConcurrentUploadJobs;
        _settings.SpeedLimit = SpeedLimitEnabled ? SpeedLimitValue : null;
        _settings.TempArchiveDirectory = TempArchiveDirectory;

        // Load accounts
        await LoadAccountsAsync(cancellationToken);
    }

    private async Task LoadAccountsAsync(CancellationToken cancellationToken = default)
    {
        Accounts.Clear();
        FileHosterLoginDto[] accounts = await _accountRepository.GetAllAsync(cancellationToken);
        foreach (FileHosterLoginDto account in accounts)
        {
            Accounts.Add(account);
        }
    }

    // ── Commands ──

    [RelayCommand]
    private void BrowseTempDirectory()
    {
        string? folder = _dialogService.BrowseFolder(TempArchiveDirectory, "Select Temp Archive Directory");
        if (folder is not null)
        {
            TempArchiveDirectory = folder;
        }
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await SaveSettingAsync(SettingKey.MaxConcurrentCPUJobs, MaxConcurrentCPUJobs.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await SaveSettingAsync(SettingKey.MaxConcurrentUploadJobs, MaxConcurrentUploadJobs.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await SaveSettingAsync(SettingKey.SpeedLimit, SpeedLimitEnabled ? SpeedLimitValue.ToString(CultureInfo.InvariantCulture) : "0", cancellationToken);
        await SaveSettingAsync(SettingKey.TempArchiveDirectory, TempArchiveDirectory, cancellationToken);

        _settings.MaxConcurrentCPUJobs = MaxConcurrentCPUJobs;
        _settings.MaxConcurrentUploadJobs = MaxConcurrentUploadJobs;
        _settings.SpeedLimit = SpeedLimitEnabled ? SpeedLimitValue : null;
        _settings.TempArchiveDirectory = TempArchiveDirectory;
    }

    [RelayCommand]
    private void AddAccountDialog()
    {
        // Open EditAccountWindow in "add" mode with empty fields
        FileHosterLoginDto newAccount = new()
        {
            FileHosterName = AvailableHosters.FirstOrDefault() ?? string.Empty,
            AccountType = AccountType.Free,
        };

        var dialog = new Views.EditAccountWindow(newAccount, AvailableHosters)
        {
            Title = "Add Account",
            Owner = System.Windows.Application.Current.MainWindow,
        };

        if (dialog.ShowDialog() == true)
        {
            _ = AddAccountFromDialogAsync(dialog.Result);
        }
    }

    private async Task AddAccountFromDialogAsync(FileHosterLoginDto dto)
    {
        // Auto-check if implementation exists
        FileHosterClient? client = FileHosterClient.FindByHost(dto.FileHosterName ?? string.Empty, Protocol.Http, _logger);
        if (client is not null)
        {
            IsCheckingAccount = true;
            CheckAccountStatus = "Verifying credentials...";

            try
            {
                AccountCheckResult result = await client.CheckAccountAsync(
                    dto.Username ?? string.Empty,
                    dto.Password ?? string.Empty);

                if (result.IsValid)
                {
                    dto.AccountType = result.AccountType;
                    dto.StatusMessage = result.Message ?? "OK";
                    CheckAccountStatus = $"Verified: {result.Message}";
                }
                else
                {
                    dto.StatusMessage = result.Message ?? "Failed";
                    CheckAccountStatus = $"Warning: {result.Message}";
                }
            }
            catch (Exception ex)
            {
                CheckAccountStatus = $"Check error: {ex.Message}";
            }
            finally
            {
                IsCheckingAccount = false;
            }
        }

        Dictionary<int, string> statuses = BuildStatusMap();
        string newStatus = dto.StatusMessage;

        await _accountRepository.InsertAsync(dto);
        CheckAccountStatus = $"Account added for {dto.FileHosterName}!";
        await LoadAccountsAsync();
        ApplyStatusMap(statuses);

        // Set status on the newly added account
        foreach (var a in Accounts)
        {
            if (a.FileHosterName == dto.FileHosterName && a.Username == dto.Username && !statuses.ContainsKey(a.Id))
            {
                a.StatusMessage = newStatus;
                UpdateAccountStatus(a.Id, newStatus);
            }
        }
    }

    [RelayCommand]
    private async Task RefreshAllAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (Accounts.Count == 0)
        {
            CheckAccountStatus = "No accounts to refresh.";
            return;
        }

        IsCheckingAccount = true;
        int checked_ = 0;
        int updated = 0;

        Dictionary<int, string> statuses = BuildStatusMap();

        foreach (FileHosterLoginDto account in Accounts.ToArray())
        {
            CheckAccountStatus = $"Checking {account.Username}@{account.FileHosterName}... ({++checked_}/{Accounts.Count})";
            UpdateAccountStatus(account.Id, "Checking...");

            FileHosterClient? client = FileHosterClient.FindByHost(account.FileHosterName ?? string.Empty, Protocol.Http, _logger);
            if (client is null)
            {
                statuses[account.Id] = "No implementation";
                UpdateAccountStatus(account.Id, "No implementation");
                continue;
            }

            try
            {
                AccountCheckResult result = await client.CheckAccountAsync(
                    account.Username ?? string.Empty,
                    account.Password ?? string.Empty,
                    cancellationToken);

                if (result.IsValid)
                {
                    statuses[account.Id] = result.Message ?? "OK";
                    if (account.AccountType != result.AccountType)
                    {
                        account.AccountType = result.AccountType;
                        updated++;
                    }
                }
                else
                {
                    statuses[account.Id] = result.Message ?? "Failed";
                }

                await _accountRepository.UpdateAsync(account, cancellationToken);
            }
            catch (Exception ex)
            {
                statuses[account.Id] = $"Error: {ex.Message}";
            }

            UpdateAccountStatus(account.Id, statuses[account.Id]);
        }

        IsCheckingAccount = false;
        await LoadAccountsAsync(cancellationToken);
        ApplyStatusMap(statuses);

        CheckAccountStatus = $"Refreshed {checked_} accounts. {updated} updated.";
    }

    [RelayCommand]
    private async Task CheckAccountAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(NewAccountHoster) || string.IsNullOrWhiteSpace(NewAccountUsername))
        {
            _dialogService.ShowError("Please fill in the file hoster and username.");
            return;
        }

        IsCheckingAccount = true;
        CheckAccountStatus = "Checking account...";

        try
        {
            FileHosterClient? client = FileHosterClient.FindByHost(NewAccountHoster, Protocol.Http, _logger);
            if (client is null)
            {
                CheckAccountStatus = $"No implementation for {NewAccountHoster}. Account will be saved without verification.";
                return;
            }

            AccountCheckResult result = await client.CheckAccountAsync(NewAccountUsername, NewAccountPassword, cancellationToken);

            if (result.IsValid)
            {
                NewAccountType = result.AccountType;
                CheckAccountStatus = $"Valid! {result.Message}";
            }
            else
            {
                CheckAccountStatus = $"Failed: {result.Message}";
            }
        }
        catch (Exception ex)
        {
            CheckAccountStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsCheckingAccount = false;
        }
    }

    [RelayCommand]
    private async Task AddAccountAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(NewAccountHoster) || string.IsNullOrWhiteSpace(NewAccountUsername))
        {
            _dialogService.ShowError("Please fill in the file hoster and username.");
            return;
        }

        // Auto-check if a client implementation exists
        FileHosterClient? client = FileHosterClient.FindByHost(NewAccountHoster, Protocol.Http, _logger);
        if (client is not null)
        {
            IsCheckingAccount = true;
            CheckAccountStatus = "Verifying credentials...";

            try
            {
                AccountCheckResult result = await client.CheckAccountAsync(NewAccountUsername, NewAccountPassword, cancellationToken);
                if (result.IsValid)
                {
                    NewAccountType = result.AccountType;
                    CheckAccountStatus = $"Verified: {result.Message}";
                }
                else
                {
                    CheckAccountStatus = $"Warning: {result.Message}";
                    if (!_dialogService.ShowConfirmation($"Account check failed: {result.Message}\n\nAdd anyway?", "Account Check"))
                    {
                        IsCheckingAccount = false;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                CheckAccountStatus = $"Check error: {ex.Message}";
                if (!_dialogService.ShowConfirmation($"Could not verify account: {ex.Message}\n\nAdd anyway?", "Account Check"))
                {
                    IsCheckingAccount = false;
                    return;
                }
            }
            finally
            {
                IsCheckingAccount = false;
            }
        }

        FileHosterLoginDto dto = new()
        {
            FileHosterName = NewAccountHoster,
            Username = NewAccountUsername,
            Password = NewAccountPassword,
            AccountType = NewAccountType,
        };

        await _accountRepository.InsertAsync(dto, cancellationToken);

        CheckAccountStatus = $"Account added for {NewAccountHoster}!";
        NewAccountUsername = string.Empty;
        NewAccountPassword = string.Empty;

        await LoadAccountsAsync(cancellationToken);
    }

    [RelayCommand]
    private async Task RemoveAccountAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedAccount is null)
        {
            return;
        }

        if (!_dialogService.ShowConfirmation($"Remove account '{SelectedAccount.Username}' for {SelectedAccount.FileHosterName}?", "Remove Account"))
        {
            return;
        }

        await _accountRepository.DeleteAsync(SelectedAccount.Id, cancellationToken);
        await LoadAccountsAsync(cancellationToken);
    }

    [RelayCommand]
    private void EditAccount()
    {
        if (SelectedAccount is null)
        {
            return;
        }

        // Open edit dialog
        var dialog = new Views.EditAccountWindow(SelectedAccount, AvailableHosters);
        dialog.Owner = System.Windows.Application.Current.MainWindow;

        if (dialog.ShowDialog() == true)
        {
            // Save changes
            _ = SaveEditedAccountAsync(dialog.Result);
        }
    }

    private async Task SaveEditedAccountAsync(FileHosterLoginDto updated)
    {
        await _accountRepository.UpdateAsync(updated);
        await LoadAccountsAsync();
    }

    [RelayCommand]
    private async Task RefreshAccountAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedAccount is null)
        {
            return;
        }

        FileHosterClient? client = FileHosterClient.FindByHost(SelectedAccount.FileHosterName ?? string.Empty, Protocol.Http, _logger);
        if (client is null)
        {
            CheckAccountStatus = $"No implementation for {SelectedAccount.FileHosterName}. Cannot check.";
            return;
        }

        IsCheckingAccount = true;
        CheckAccountStatus = $"Checking {SelectedAccount.Username}@{SelectedAccount.FileHosterName}...";

        // Show "Checking..." in the DataGrid immediately
        UpdateAccountStatus(SelectedAccount.Id, "Checking...");

        try
        {
            AccountCheckResult result = await client.CheckAccountAsync(
                SelectedAccount.Username ?? string.Empty,
                SelectedAccount.Password ?? string.Empty,
                cancellationToken);

            string statusMsg;
            if (result.IsValid)
            {
                SelectedAccount.AccountType = result.AccountType;
                statusMsg = result.Message ?? "OK";
                CheckAccountStatus = $"Valid: {result.Message}";
            }
            else
            {
                statusMsg = result.Message ?? "Failed";
                CheckAccountStatus = $"Failed: {result.Message}";
            }

            await _accountRepository.UpdateAsync(SelectedAccount, cancellationToken);

            // Reload and preserve status messages
            Dictionary<int, string> statuses = BuildStatusMap();
            statuses[SelectedAccount.Id] = statusMsg;
            await LoadAccountsAsync(cancellationToken);
            ApplyStatusMap(statuses);
        }
        catch (Exception ex)
        {
            CheckAccountStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsCheckingAccount = false;
        }
    }

    [RelayCommand]
    private async Task ToggleAccountAsync(string? parameter, CancellationToken cancellationToken = default)
    {
        if (SelectedAccount is null)
        {
            return;
        }

        bool disable = !string.Equals(parameter, "Enable", StringComparison.Ordinal);
        string username = SelectedAccount.Username ?? "unknown";
        int accountId = SelectedAccount.Id;
        SelectedAccount.Disabled = disable;
        await _accountRepository.UpdateAsync(SelectedAccount, cancellationToken);

        Dictionary<int, string> statuses = BuildStatusMap();
        await LoadAccountsAsync(cancellationToken);
        ApplyStatusMap(statuses);

        CheckAccountStatus = disable
            ? $"Account '{username}' disabled."
            : $"Account '{username}' enabled.";
    }

    // ── Helpers for preserving StatusMessage across reloads ──

    private Dictionary<int, string> BuildStatusMap()
        => Accounts.ToDictionary(a => a.Id, a => a.StatusMessage);

    private void ApplyStatusMap(Dictionary<int, string> statuses)
    {
        foreach (var a in Accounts)
        {
            if (statuses.TryGetValue(a.Id, out string? msg))
            {
                a.StatusMessage = msg;
            }
        }
    }

    /// <summary>
    /// Updates the StatusMessage on an account in the collection and replaces
    /// the item to trigger the ObservableCollection change notification
    /// (since FileHosterLoginDto doesn't implement INotifyPropertyChanged).
    /// </summary>
    private void UpdateAccountStatus(int accountId, string status)
    {
        for (int i = 0; i < Accounts.Count; i++)
        {
            if (Accounts[i].Id == accountId)
            {
                Accounts[i].StatusMessage = status;

                // Replace item in-place to trigger collection change → UI refresh
                FileHosterLoginDto item = Accounts[i];
                Accounts[i] = item;
                return;
            }
        }
    }

    private async Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        SettingDto? existing = await _settingRepository.FindByKeyAsync(key, cancellationToken);

        if (existing is not null)
        {
            existing.Value = value;
            await _settingRepository.UpdateAsync(existing, cancellationToken);
        }
        else
        {
            await _settingRepository.InsertAsync(new SettingDto { Key = key, Value = value }, cancellationToken);
        }
    }
}
