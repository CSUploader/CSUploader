// <copyright file="ToastNotificationService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Lib.Localization;
using CSUploader.Upload;
using CSUploader.ViewModels;

namespace CSUploader.Services;

/// <summary>
/// Stacks bottom-right toast popups for upload completions. Reads the
/// <see cref="AppSettings.ShowCompletionToasts"/> gate; silently no-ops when off.
/// New toasts appear above the existing stack; closing one re-flows the rest down.
/// </summary>
public sealed class ToastNotificationService : IToastNotificationService
{
    private const double ToastWidth = 360;
    private const double Margin = 12;

    private readonly AppSettings _settings;
    private readonly IToastWindowFactory _factory;
    private readonly Func<Rect> _workAreaProvider;
    private readonly Action _activate;
    private readonly List<IToastHost> _activeToasts = new();

    public ToastNotificationService(
        AppSettings settings,
        IToastWindowFactory factory,
        Func<Rect> workAreaProvider,
        Action activate)
    {
        _settings = settings;
        _factory = factory;
        _workAreaProvider = workAreaProvider;
        _activate = activate;
    }

    public void ShowFileCompleted(PackageFile file)
    {
        if (!_settings.ShowCompletionToasts) return;
        ShowToast(
            title: Localizer.Instance["Toast_FileCompleted_Title"],
            message: file.Name,
            iconKey: "StatusSuccessImage");
    }

    public void ShowPackageCompleted(Package package, int succeeded, int total)
    {
        if (!_settings.ShowCompletionToasts) return;

        string body = string.Format(
            CultureInfo.CurrentCulture,
            Localizer.Instance["Toast_PackageCompleted_Body"],
            succeeded,
            total,
            package.Name);

        ShowToast(
            title: Localizer.Instance["Toast_PackageCompleted_Title"],
            message: body,
            iconKey: "PackageClosedImage");
    }

    private void ShowToast(string title, string message, string iconKey)
    {
        // Reference cell so the VM's commands can capture the host without needing it
        // to exist before the VM is built. Assigned right after factory.Create.
        IToastHost? host = null;

        ToastViewModel vm = new(
            activateCommand: new RelayCommand(() =>
            {
                _activate();
                host?.Close();
            }),
            closeCommand: new RelayCommand(() => host?.Close()))
        {
            Title = title,
            Message = message,
            IconKey = iconKey,
        };

        host = _factory.Create(vm);
        host.Closed += (_, _) =>
        {
            _activeToasts.Remove(host);
            Reflow();
        };

        _activeToasts.Add(host);
        Reflow();
        host.Show();
    }

    private void Reflow()
    {
        Rect work = _workAreaProvider();
        double cumulative = 0;
        foreach (IToastHost h in _activeToasts)
        {
            cumulative += h.Height;
            h.Top = work.Bottom - Margin - cumulative;
            h.Left = work.Right - ToastWidth - Margin;
        }
    }
}
