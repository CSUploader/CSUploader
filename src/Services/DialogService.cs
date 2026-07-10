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
using Ookii.Dialogs.Wpf;

namespace CSUploader.Services;

public class DialogService(AppSettings settings, SettingRepository settingRepository)
    : DialogServiceBase(settings, settingRepository), IDialogService
{
    // Parent every dialog to the currently-active window rather than the main window: a dialog
    // opened from the modal upload wizard must own the wizard (WPF thread-modality would otherwise
    // render the child invisible / off-centre). Mirrors ConfirmationDialog's owner resolution.
    private static Window? ActiveOwner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

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

    protected override Task<(bool Confirmed, bool DontAskAgain)> ShowOptOutConfirmationCoreAsync(string message, string title)
    {
        ConfirmationDialog dialog = new(message, title);
        bool? result = dialog.ShowDialog();
        bool confirmed = result == true && dialog.Confirmed;
        return Task.FromResult((confirmed, dialog.DontAskAgain));
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

    public Task<string?> BrowseOpenFileAsync(string? title = null, string? filter = null, string? defaultExt = null)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = title ?? Localizer.Instance["Common_SelectFiles"],
        };

        if (!string.IsNullOrEmpty(filter))
        {
            dialog.Filter = filter;
        }

        if (!string.IsNullOrEmpty(defaultExt))
        {
            dialog.DefaultExt = defaultExt;
        }

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    public Task<string?> BrowseSaveFileAsync(string? suggestedFileName = null, string? filter = null, string? defaultExt = null)
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            AddExtension = true,
        };

        if (!string.IsNullOrEmpty(suggestedFileName))
        {
            dialog.FileName = suggestedFileName;
        }

        if (!string.IsNullOrEmpty(filter))
        {
            dialog.Filter = filter;
        }

        if (!string.IsNullOrEmpty(defaultExt))
        {
            dialog.DefaultExt = defaultExt;
        }

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    public Task<FileHosterLoginDto?> ShowAddAccountDialogAsync(string hosterName, string[] availableHosters, Func<string, Task<AccountCheckResult>> interactiveLogin, string? title = null)
    {
        FileHosterLoginDto seed = new()
        {
            FileHosterName = hosterName,
            AccountType = AccountType.Free,
        };

        EditAccountWindow dialog = new(seed, availableHosters, interactiveLogin)
        {
            Title = title ?? Localizer.Instance["EditAccount_AddTitle"],
            Owner = ActiveOwner,
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Result : null);
    }

    public Task<ProxySettingDto?> ShowEditProxyDialogAsync(ProxySettingDto seed, string? title = null)
    {
        EditProxyWindow dialog = new(seed, Settings.AllowInvalidServerCertificates)
        {
            Title = title ?? Localizer.Instance[seed.Id == 0 ? "EditProxy_AddTitle" : "EditProxy_EditTitle"],
            Owner = ActiveOwner,
        };

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Result : null);
    }

    public Task ShowHttpDetailsAsync(HttpTransaction transaction)
    {
        HttpDetailsWindow window = new(transaction)
        {
            Owner = ActiveOwner,
        };
        window.ShowDialog();
        return Task.CompletedTask;
    }

    public Task<string?> ShowProxyTextDialogAsync(string title, string description, string initialText, bool readOnly)
    {
        ProxyTextDialog dialog = new(title, description, initialText, readOnly)
        {
            Owner = ActiveOwner,
        };

        // Editable mode returns the typed text on OK / null on cancel; read-only (export) mode
        // has no OK button so this always yields null, which the export caller ignores.
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.ResultText : null);
    }

    public Task<SpeedLimitSelection?> ShowSpeedLimitDialogAsync(int? currentLimit)
    {
        SpeedLimitDialog dialog = new(currentLimit)
        {
            Owner = ActiveOwner,
        };

        // Distinguish Cancel (null) from Clear (a selection whose LimitKBps is null) — the
        // dialog's int? Result conflates "cleared" with "unset", so we key off DialogResult.
        return Task.FromResult(dialog.ShowDialog() == true ? new SpeedLimitSelection(dialog.Result) : (SpeedLimitSelection?)null);
    }

    public Task<FileHosterLoginDto?> ShowEditAccountDialogAsync(FileHosterLoginDto account, string[] hosters, Func<string, Task<AccountCheckResult>> interactiveLogin, string? title = null)
    {
        EditAccountWindow dialog = new(account, hosters, interactiveLogin)
        {
            Owner = ActiveOwner,
        };

        // The add flow passes an explicit title; the edit flow passes null so the window keeps
        // its XAML-defined default title.
        if (title is not null)
        {
            dialog.Title = title;
        }

        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Result : null);
    }
}
