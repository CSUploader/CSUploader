// <copyright file="EditProxyWindowTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CSUploader.Dal;
using CSUploader.Lib.Net;
using CSUploader.Views;
using static CSUploader.Tests.Avalonia.HeadlessInput;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless behavior tests for <see cref="EditProxyWindow"/> (Phase 5 Task 8). The load-bearing checks are
/// the Save DTO mapping (type/host/port/user/pass/enabled, empty user/pass → null, Id + Priority carried
/// from the seed), the result contract through <c>ShowDialog&lt;ProxySettingDto?&gt;</c> (Save-valid → the
/// DTO, Cancel/Esc → null), the validation flow (bad port / bad host keep the dialog open behind a warning
/// message box), and the prep-item-9 password masking. The Test button is deliberately NOT driven here: it
/// fires a real socket through <c>ProxyManager.TestProxyAsync</c>, and its input rules are the shared
/// <c>TryBuildDtoFromFields</c> surface the Save tests already cover. Every shown window is closed in a
/// <c>finally</c> (headless windows are process-global for the session).
/// </summary>
public class EditProxyWindowTests
{
    [AvaloniaFact]
    public async Task Save_ValidFields_ReturnsMappedDto_CarryingIdAndPriority()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var seed = new ProxySettingDto { Id = 7, Type = ProxyType.Http, Host = "10.0.0.1", Port = 1080, Priority = 3 };
        var dlg = new EditProxyWindow(seed);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<ProxySettingDto?> dialog = dlg.ShowDialog<ProxySettingDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            dlg.TypeCombo.SelectedItem = ProxyType.Socks5;
            dlg.HostBox.Text = "proxy.example.test";
            dlg.PortBox.Text = "8888";
            dlg.UsernameBox.Text = "user1";
            dlg.PasswordBox.Text = "secret";
            dlg.EnabledCheck.IsChecked = true;
            Click(dlg.SaveButton);
            Dispatcher.UIThread.RunJobs();

            ProxySettingDto? result = await dialog;
            Assert.NotNull(result);
            Assert.Equal(ProxyType.Socks5, result!.Type);
            Assert.Equal("proxy.example.test", result.Host);
            Assert.Equal(8888, result.Port);
            Assert.Equal("user1", result.Username);
            Assert.Equal("secret", result.Password);
            Assert.True(result.Enabled);
            Assert.Equal(7, result.Id);       // carried from the seed, not re-derived
            Assert.Equal(3, result.Priority); // carried from the seed
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Save_EmptyUsernameAndPassword_MapToNull()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditProxyWindow(new ProxySettingDto { Type = ProxyType.Http, Host = "127.0.0.1", Port = 8080 });
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<ProxySettingDto?> dialog = dlg.ShowDialog<ProxySettingDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            dlg.UsernameBox.Text = string.Empty;
            dlg.PasswordBox.Text = string.Empty;
            Click(dlg.SaveButton);
            Dispatcher.UIThread.RunJobs();

            ProxySettingDto? result = await dialog;
            Assert.NotNull(result);
            Assert.Null(result!.Username); // empty → null (WPF parity), not "" persisted
            Assert.Null(result.Password);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task CancelButton_ReturnsNull()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditProxyWindow(new ProxySettingDto { Type = ProxyType.Http, Host = "127.0.0.1", Port = 8080 });
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<ProxySettingDto?> dialog = dlg.ShowDialog<ProxySettingDto?>(owner);
            Dispatcher.UIThread.RunJobs();
            Click(dlg.CancelButton); // CancelButton_Click → Close(null)
            Dispatcher.UIThread.RunJobs();

            ProxySettingDto? result = await dialog;
            Assert.Null(result);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Esc_RoutesThroughIsCancel_ReturnsNull()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditProxyWindow(new ProxySettingDto { Type = ProxyType.Http, Host = "127.0.0.1", Port = 8080 });
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();

            Task<ProxySettingDto?> dialog = dlg.ShowDialog<ProxySettingDto?>(owner);
            Dispatcher.UIThread.RunJobs();
            Press(dlg, Key.Escape, PhysicalKey.Escape); // IsCancel → CancelButton_Click → Close(null)
            Dispatcher.UIThread.RunJobs();

            ProxySettingDto? result = await dialog;
            Assert.Null(result);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void Save_PortOutOfRange_KeepsDialogOpen_AndShowsMessageBox()
    {
        // Shown non-modally so the validation box can own over it (owner = this), mirroring the WPF
        // SpeedLimitDialog port's invalid-input test.
        var dlg = new EditProxyWindow(new ProxySettingDto { Type = ProxyType.Http, Host = "127.0.0.1", Port = 8080 });
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            dlg.PortBox.Text = "99999"; // > 65535
            Click(dlg.SaveButton);
            Dispatcher.UIThread.RunJobs();

            Assert.True(dlg.IsVisible); // did not close — invalid port
            MessageBoxWindow? box = dlg.OwnedWindows.OfType<MessageBoxWindow>().FirstOrDefault();
            Assert.NotNull(box); // a validation warning appeared, owned by the dialog

            box!.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(dlg.IsVisible); // still open after dismissing the warning
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void Save_NonNumericPort_KeepsDialogOpen_AndShowsMessageBox()
    {
        var dlg = new EditProxyWindow(new ProxySettingDto { Type = ProxyType.Http, Host = "127.0.0.1", Port = 8080 });
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            dlg.PortBox.Text = "abc"; // not an integer
            Click(dlg.SaveButton);
            Dispatcher.UIThread.RunJobs();

            Assert.True(dlg.IsVisible);
            Assert.NotNull(dlg.OwnedWindows.OfType<MessageBoxWindow>().FirstOrDefault());
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void Save_EmptyHost_KeepsDialogOpen_AndShowsMessageBox()
    {
        var dlg = new EditProxyWindow(new ProxySettingDto { Type = ProxyType.Http, Host = "127.0.0.1", Port = 8080 });
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            dlg.HostBox.Text = "   "; // trims to empty → host required
            Click(dlg.SaveButton);
            Dispatcher.UIThread.RunJobs();

            Assert.True(dlg.IsVisible);
            Assert.NotNull(dlg.OwnedWindows.OfType<MessageBoxWindow>().FirstOrDefault());
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void PasswordBox_IsMasked()
    {
        // Prep item 9: the recorded deviation from WPF's cleartext box — PasswordChar is the only masking
        // lever (the box is populated from code-behind, no VM binding for the bridge redactor to key on).
        var dlg = new EditProxyWindow(new ProxySettingDto { Type = ProxyType.Http, Host = "127.0.0.1", Port = 8080, Password = "secret" });
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal('●', dlg.PasswordBox.PasswordChar);
        }
        finally
        {
            dlg.Close();
        }
    }
}
