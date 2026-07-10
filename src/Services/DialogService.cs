// <copyright file="DialogService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using CSUploader.Dal;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Views;
using Microsoft.Extensions.DependencyInjection;
using Ookii.Dialogs.Wpf;

namespace CSUploader.Services;

// IAccountVerifier is resolved lazily via IServiceProvider rather than constructor-injected.
// Direct injection would close a DI cycle: DialogService → IAccountVerifier → IFileHosterRegistry
// → IFileHosterPipeline[] → ExLoadPipeline → IInteractiveAuthService → WebViewInteractiveAuthService
// → IDialogService. MS.Extensions.DependencyInjection's cycle detector only sees constructor-arg
// edges; the cycle here closes through `sp.GetServices<IFileHosterPipeline>()` inside the
// IFileHosterRegistry factory, which the detector treats as opaque — so instead of throwing on
// startup it loops infinitely (no main window, process pegs CPU). Resolving the verifier at
// click-time (the Sign-in button) breaks the cycle: by then the graph is fully built and the
// lookup is a simple lazy fetch.
public class DialogService(AppSettings settings, SettingRepository settingRepository, IServiceProvider services) : IDialogService
{
    // These stay fully synchronous under the covers — WPF dialogs are modal and block on the
    // UI thread. The interface is Task-returning only so the Avalonia head (whose dialogs are
    // genuinely async) can share it; here we wrap the completed result. No Task.Run: launching
    // a dialog off the UI thread would throw.
    public Task ShowErrorAsync(string message, string? title = null)
    {
        MessageBox.Show(message, title ?? Localizer.Instance["Common_Error"], MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.CompletedTask;
    }

    public Task<bool> ShowConfirmationAsync(string message, string? title = null)
    {
        MessageBoxResult result = MessageBox.Show(message, title ?? Localizer.Instance["Common_Confirm"], MessageBoxButton.YesNo, MessageBoxImage.Question);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public Task<bool> ShowOptOutConfirmationAsync(string confirmationKey, string message, string? title = null)
    {
        if (settings.SuppressedConfirmations.Contains(confirmationKey))
        {
            return Task.FromResult(true);
        }

        ConfirmationDialog dialog = new(message, title ?? Localizer.Instance["Common_Confirm"]);
        bool? result = dialog.ShowDialog();
        if (result != true || !dialog.Confirmed)
        {
            return Task.FromResult(false);
        }

        if (dialog.DontAskAgain)
        {
            settings.SuppressedConfirmations.Add(confirmationKey);

            // Fire-and-forget the DB write; if it fails the user will just be asked again
            // on next action, which is an acceptable fallback.
            _ = PersistSuppressedAsync();
        }

        return Task.FromResult(true);
    }

    public Task<string?> BrowseFolderAsync(string? initialDirectory = null, string? title = null)
    {
        VistaFolderBrowserDialog dialog = new()
        {
            Description = title ?? Localizer.Instance["Common_SelectFolder"],
            UseDescriptionForTitle = true,
        };

        if (!string.IsNullOrEmpty(initialDirectory))
        {
            dialog.SelectedPath = initialDirectory;
        }

        bool? dialogResult = dialog.ShowDialog();
        return Task.FromResult(dialogResult == true ? dialog.SelectedPath : null);
    }

    public Task<string[]?> BrowseFilesAsync(string? title = null, string? filter = null)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = title ?? Localizer.Instance["Common_SelectFiles"],
            Multiselect = true,
            CheckFileExists = true,
        };

        if (!string.IsNullOrEmpty(filter))
        {
            dialog.Filter = filter;
        }

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileNames : null);
    }

    public Task<FileHosterLoginDto?> ShowAddAccountDialogAsync(string hosterName, string[] availableHosters, string? title = null)
    {
        FileHosterLoginDto seed = new()
        {
            FileHosterName = hosterName,
            AccountType = AccountType.Free,
        };

        EditAccountWindow dialog = new(
            seed,
            availableHosters,
            // Interactive sign-in for XFileSharing-API hosters: runs the no-API-key verify
            // flow (captcha WebView → my_account scrape → derive key). Same call the
            // Settings VM wires in for its own add/edit dialogs. Resolved lazily — see
            // the class comment for why direct ctor injection would deadlock startup.
            hoster => services.GetRequiredService<IAccountVerifier>().CheckAsync(hoster, string.Empty, string.Empty, null))
        {
            Title = title ?? Localizer.Instance["EditAccount_AddTitle"],
            Owner = Application.Current.MainWindow,
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Result : null);
    }

    public Task<ProxySettingDto?> ShowEditProxyDialogAsync(ProxySettingDto seed, string? title = null)
    {
        EditProxyWindow dialog = new(seed, settings.AllowInvalidServerCertificates)
        {
            Title = title ?? Localizer.Instance[seed.Id == 0 ? "EditProxy_AddTitle" : "EditProxy_EditTitle"],
            Owner = Application.Current.MainWindow,
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Result : null);
    }

    public Task ShowHttpDetailsAsync(HttpTransaction transaction)
    {
        HttpDetailsWindow window = new(transaction)
        {
            Owner = Application.Current?.MainWindow,
        };
        window.ShowDialog();
        return Task.CompletedTask;
    }

    public Task<string?> ShowProxyTextDialogAsync(string title, string description, string initialText, bool readOnly)
    {
        ProxyTextDialog dialog = new(title, description, initialText, readOnly)
        {
            Owner = Application.Current?.MainWindow,
        };

        // Editable mode returns the typed text on OK / null on cancel; read-only (export) mode
        // has no OK button so this always yields null, which the export caller ignores.
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.ResultText : null);
    }

    public Task<SpeedLimitSelection?> ShowSpeedLimitDialogAsync(int? currentLimit)
    {
        SpeedLimitDialog dialog = new(currentLimit)
        {
            Owner = Application.Current.MainWindow,
        };

        // Distinguish Cancel (null) from Clear (a selection whose LimitKBps is null) — the
        // dialog's int? Result conflates "cleared" with "unset", so we key off DialogResult.
        return Task.FromResult(dialog.ShowDialog() == true ? new SpeedLimitSelection(dialog.Result) : (SpeedLimitSelection?)null);
    }

    public Task<FileHosterLoginDto?> ShowEditAccountDialogAsync(FileHosterLoginDto account, string[] hosters, Func<string, Task<AccountCheckResult>> interactiveLogin, string? title = null)
    {
        EditAccountWindow dialog = new(account, hosters, interactiveLogin)
        {
            Owner = Application.Current.MainWindow,
        };

        // The add flow passes an explicit title; the edit flow passes null so the window keeps
        // its XAML-defined default title.
        if (title is not null)
        {
            dialog.Title = title;
        }

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Result : null);
    }

    private async Task PersistSuppressedAsync()
    {
        try
        {
            string value = string.Join(",", settings.SuppressedConfirmations);
            SettingDto? existing = await settingRepository.FindByKeyAsync(SettingKey.SuppressedConfirmations);
            if (existing is not null)
            {
                existing.Value = value;
                await settingRepository.UpdateAsync(existing);
            }
            else
            {
                await settingRepository.InsertAsync(new SettingDto { Key = SettingKey.SuppressedConfirmations, Value = value });
            }
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
