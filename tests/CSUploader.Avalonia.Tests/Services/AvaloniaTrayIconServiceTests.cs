// <copyright file="AvaloniaTrayIconServiceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Headless.XUnit;
using CSUploader.Lib;
using CSUploader.Services;
using CSUploader.Upload;
using Moq;

namespace CSUploader.Tests.Avalonia.Services;

public class AvaloniaTrayIconServiceTests
{
    private sealed class RecordingToasts : IToastNotificationService
    {
        public int InfoCount { get; private set; }

        public void ShowFileCompleted(PackageFile file)
        {
        }

        public void ShowPackageCompleted(Package package, int succeeded, int total)
        {
        }

        public void ShowInfo(string title, string body) => InfoCount++;
    }

    [Fact]
    public void NotifyHidden_ShowsInfoToast_OncePerSession()
    {
        var toasts = new RecordingToasts();
        var svc = new AvaloniaTrayIconService(new AppSettings(), Mock.Of<IAppLogger>(), toasts);

        svc.NotifyHidden();
        svc.NotifyHidden(); // second hide in the same session is silent

        Assert.Equal(1, toasts.InfoCount);
    }

    [Fact]
    public void NotifyHidden_AfterDispose_DoesNothing()
    {
        var toasts = new RecordingToasts();
        var svc = new AvaloniaTrayIconService(new AppSettings(), Mock.Of<IAppLogger>(), toasts);
        svc.Dispose();

        svc.NotifyHidden();

        Assert.Equal(0, toasts.InfoCount);
    }

    [AvaloniaFact]
    public void EnsureIconForSession_CreatesIcon_EvenWhenSettingsSayNoMinimize()
    {
        // Strand-fix (Phase 9 ledger a): Ask->Minimize without Remember must keep the icon for the session
        // even though settings still say don't-minimize, so UpdateVisibility() would tear it down.
        AppSettings settings = new() { MinimizeToTray = false, CloseAction = CloseAction.Ask };
        AvaloniaTrayIconService tray = new(settings, Mock.Of<IAppLogger>(), Mock.Of<IToastNotificationService>());
        try
        {
            tray.UpdateVisibility();           // settings say no icon…
            Assert.False(tray.HasIcon);
            tray.EnsureIconForSession();        // …but the session-force creates it anyway.
            Assert.True(tray.HasIcon);
        }
        finally
        {
            tray.Dispose();
        }
    }
}
