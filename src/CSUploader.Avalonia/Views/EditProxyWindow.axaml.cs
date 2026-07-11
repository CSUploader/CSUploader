// <copyright file="EditProxyWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Views;

/// <summary>
/// Modal dialog for adding (and editing) a single proxy (port of the WPF <c>EditProxyWindow</c>). The
/// code-behind is the WPF one ported near-verbatim: the same field seeding, the same
/// <see cref="ProxyManager.TestProxyAsync"/> / <see cref="ProxyTestResult"/> test flow (Core,
/// framework-free), and the same host/port validation. The Avalonia deltas: the result is carried through
/// <c>ShowDialog&lt;ProxySettingDto?&gt;</c> — Save-valid → <see cref="Window.Close(object?)"/> with the
/// DTO, Cancel/Esc/X → <c>Close(null)</c> — collapsing the WPF <c>Result</c> + <c>DialogResult</c> pair
/// (rule 6); Esc reaches Cancel through <c>IsCancel</c> which no longer auto-closes, so the handler closes
/// explicitly (rule 7); the test-status colour is a toggled class, not a code-behind brush swap (rule 29);
/// and validation warnings show through the async <see cref="MessageBoxWindow"/> (so the Save/Test handlers
/// are <c>async void</c>, the same shape the WPF <c>SpeedLimitDialog</c> port used).
/// </summary>
public partial class EditProxyWindow : Window
{
    private readonly ProxySettingDto _original;
    private readonly bool _acceptInvalidCertificates;
    private HttpTransaction? _lastTestTransaction;

    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001); the app always uses the
    // proxy/certificate overload. Seeds an empty proxy so the field-seeding below is null-safe.
    public EditProxyWindow()
        : this(new ProxySettingDto())
    {
    }

    public EditProxyWindow(ProxySettingDto proxy, bool acceptInvalidCertificates = false)
    {
        InitializeComponent();

        _original = proxy;
        _acceptInvalidCertificates = acceptInvalidCertificates;

        TypeCombo.ItemsSource = ConnectionManagerProxyTypes;
        TypeCombo.SelectedItem = proxy.Type;

        HostBox.Text = proxy.Host;
        PortBox.Text = proxy.Port.ToString(CultureInfo.InvariantCulture);
        UsernameBox.Text = proxy.Username;
        PasswordBox.Text = proxy.Password;
        EnabledCheck.IsChecked = proxy.Enabled;

        // Focus moves to Opened: controls aren't attached to the visual tree at ctor time in Avalonia (port
        // rule 16), so focusing HostBox in the ctor would no-op.
        Opened += (_, _) => HostBox.Focus();
    }

    /// <summary>Same option set the inline ComboBox in the proxy grid uses.</summary>
    private static ProxyType[] ConnectionManagerProxyTypes { get; } =
        [ProxyType.None, ProxyType.Http, ProxyType.Https, ProxyType.Socks4, ProxyType.Socks5];

    // Save/Esc/Cancel are async void because validation may await the custom message box; the WPF
    // SpeedLimitDialog port established this shape.
    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        ProxySettingDto? dto = await TryBuildDtoFromFieldsAsync();
        if (dto is null)
        {
            return;
        }

        Close(dto);
    }

    // Cancel/Esc → null. WPF's Cancel button had no handler (IsCancel auto-closed with DialogResult=false);
    // Avalonia's IsCancel only routes Esc to Click without closing (port rule 7), so close explicitly.
    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);

    private async void TestButton_Click(object? sender, RoutedEventArgs e)
    {
        ProxySettingDto? dto = await TryBuildDtoFromFieldsAsync();
        if (dto is null)
        {
            return;
        }

        // Disable Save while the test runs so we don't race with a close mid-request, and disable the Test
        // button itself to prevent multiple concurrent tests. Also hide any stale Details button from a prior
        // test until the new transaction is available.
        TestButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        _lastTestTransaction = null;
        TestDetailsButton.IsVisible = false;
        SetStatus(Localizer.Instance["EditProxy_Status_Testing"], isError: false);

        try
        {
            ProxyTestResult result = await ProxyManager.TestProxyAsync(dto, Logger.Current, acceptInvalidCertificates: _acceptInvalidCertificates).ConfigureAwait(true);

            if (result.Success)
            {
                string msg = string.IsNullOrEmpty(result.DetectedIp)
                    ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["EditProxy_Status_OkLatency_Format"], result.LatencyMs)
                    : string.Format(CultureInfo.CurrentCulture, Localizer.Instance["EditProxy_Status_OkLatencyIp_Format"], result.LatencyMs, result.DetectedIp);
                SetStatus(msg, isError: false);
            }
            else
            {
                string firstLine = (result.Message ?? string.Empty).Split('\n')[0];
                if (firstLine.Length > 200)
                {
                    firstLine = firstLine[..200] + "…";
                }

                SetStatus(string.Format(CultureInfo.CurrentCulture, Localizer.Instance["EditProxy_Status_Failed_Format"], firstLine), isError: true);
            }

            // Surface the request/response details so the user can triage failures (mis-rendered HTML pages,
            // auth challenges, etc.) without leaving the dialog. Only show the button when there's actually a
            // transaction — most exceptions short-circuit before any HTTP round-trip lands.
            _lastTestTransaction = result.Transaction;
            TestDetailsButton.IsVisible = _lastTestTransaction is not null;
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(CultureInfo.CurrentCulture, Localizer.Instance["EditProxy_Status_Failed_Format"], ex.Message), isError: true);
        }
        finally
        {
            TestButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
    }

    private void TestDetailsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastTestTransaction is null)
        {
            return;
        }

        HttpDetailsWindow window = new(_lastTestTransaction);
        _ = window.ShowDialog(this);
    }

    private void SetStatus(string message, bool isError)
    {
        TestStatusText.Text = message;
        TestStatusText.Classes.Set("error", isError);
        TestStatusText.IsVisible = true;
    }

    /// <summary>
    /// Reads the field values, runs the same validation Save uses, and returns a populated DTO. On validation
    /// failure it shows a warning message box, focuses the bad field, and returns null. Used by both Save and
    /// Test so a Test attempt is held to the same input rules as a Save.
    /// </summary>
    private async Task<ProxySettingDto?> TryBuildDtoFromFieldsAsync()
    {
        string host = HostBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(host))
        {
            await MessageBoxWindow.ShowErrorAsync(this, Localizer.Instance["EditProxy_Validation_HostRequired"], Localizer.Instance["Common_Error"]);
            HostBox.Focus();
            return null;
        }

        if (!int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
            || port < 1
            || port > 65535)
        {
            await MessageBoxWindow.ShowErrorAsync(this, Localizer.Instance["EditProxy_Validation_PortInvalid"], Localizer.Instance["Common_Error"]);
            PortBox.Focus();
            PortBox.SelectAll();
            return null;
        }

        return new ProxySettingDto
        {
            Id = _original.Id,
            Type = TypeCombo.SelectedItem is ProxyType t ? t : ProxyType.Http,
            Host = host,
            Port = port,
            Username = string.IsNullOrEmpty(UsernameBox.Text) ? null : UsernameBox.Text,
            Password = string.IsNullOrEmpty(PasswordBox.Text) ? null : PasswordBox.Text,
            Enabled = EnabledCheck.IsChecked == true,
            Priority = _original.Priority,
        };
    }
}
