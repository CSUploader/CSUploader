// <copyright file="ToastNotificationService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
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
/// <remarks>
/// Initializes a new instance of <see cref="ToastNotificationService"/>.
/// </remarks>
/// <param name="settings">Application settings.</param>
/// <param name="factory">Factory for creating toast host windows.</param>
/// <param name="workAreaProvider">Returns the primary-screen work area in DIPs, used for positioning.</param>
/// <param name="activate">Callback invoked when the user clicks a toast to bring the main window forward.</param>
/// <param name="dispatcher">Marshals stacking/re-flow work onto the UI thread via <see cref="IUiDispatcher.Post"/>.</param>
public sealed class ToastNotificationService(
    AppSettings settings,
    IToastWindowFactory factory,
    Func<DipRect> workAreaProvider,
    Action activate,
    IUiDispatcher dispatcher) : IToastNotificationService
{
    internal const double ToastWidth = 360;
    internal const double Margin = 12;
    private readonly List<IToastHost> _activeToasts = [];

    public void ShowFileCompleted(PackageFile file)
    {
        if (!settings.ShowCompletionToasts)
        {
            return;
        }

        string body = string.Format(
            CultureInfo.CurrentCulture,
            Localizer.Instance["Toast_FileCompleted_Body"],
            file.Name);
        dispatcher.Post(() => ShowToast(
            title: Localizer.Instance["Toast_FileCompleted_Title"],
            message: body,
            iconKey: "StatusSuccessImage"));
    }

    public void ShowPackageCompleted(Package package, int succeeded, int total)
    {
        if (!settings.ShowCompletionToasts)
        {
            return;
        }

        string body = string.Format(
            CultureInfo.CurrentCulture,
            Localizer.Instance["Toast_PackageCompleted_Body"],
            succeeded,
            total,
            package.Name);

        dispatcher.Post(() => ShowToast(
            title: Localizer.Instance["Toast_PackageCompleted_Title"],
            message: body,
            iconKey: "PackageClosedImage"));
    }

    private void ShowToast(string title, string message, string iconKey)
    {
        // Reference cell so the VM's commands can capture the host without needing it
        // to exist before the VM is built. Assigned right after factory.Create.
        IToastHost? host = null;

        ToastViewModel vm = new(
            activateCommand: new RelayCommand(() =>
            {
                activate();
                host?.Close();
            }),
            closeCommand: new RelayCommand(() => host?.Close()))
        {
            Title = title,
            Message = message,
            IconKey = iconKey,
        };

        host = factory.Create(vm);
        host.Closed += (_, _) => dispatcher.Post(() =>
        {
            _activeToasts.Remove(host);
            Reflow();
        });

        _activeToasts.Add(host);
        Reflow();
        host.Show();
    }

    private void Reflow()
    {
        DipRect work = workAreaProvider();
        double cumulative = 0;
        foreach (IToastHost h in _activeToasts)
        {
            cumulative += h.Height;
            h.Top = work.Bottom - Margin - cumulative;
            h.Left = work.Right - ToastWidth - Margin;
        }
    }
}
