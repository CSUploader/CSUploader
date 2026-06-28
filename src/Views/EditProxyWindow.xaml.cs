// <copyright file="EditProxyWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Views;

/// <summary>
/// Modal dialog for adding (and, in future, editing) a single proxy. Replaces the
/// previous "add an empty row to the grid and let the user tab through cells" flow,
/// which was unfriendly and bypassed input validation.
/// </summary>
public partial class EditProxyWindow : Window
{
    private readonly ProxySettingDto _original;
    private readonly bool _acceptInvalidCertificates;
    private HttpTransaction? _lastTestTransaction;

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

        Loaded += (_, _) => HostBox.Focus();
    }

    public ProxySettingDto? Result { get; private set; }

    /// <summary>Same option set the inline ComboBox in the proxy grid uses.</summary>
    private static ProxyType[] ConnectionManagerProxyTypes { get; } =
        [ProxyType.None, ProxyType.Http, ProxyType.Https, ProxyType.Socks4, ProxyType.Socks5];

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildDtoFromFields(out ProxySettingDto? dto))
        {
            return;
        }

        Result = dto;
        DialogResult = true;
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildDtoFromFields(out ProxySettingDto? dto))
        {
            return;
        }

        // Disable Save while the test runs so we don't race with a close mid-request,
        // and disable the Test button itself to prevent multiple concurrent tests. Also
        // hide any stale Details button from a prior test until the new transaction is
        // available.
        TestButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        _lastTestTransaction = null;
        TestDetailsButton.Visibility = Visibility.Collapsed;
        SetStatus(Localizer.Instance["EditProxy_Status_Testing"], isError: false);

        try
        {
            ProxyTestResult result = await ProxyManager.TestProxyAsync(dto!, Logger.Current, acceptInvalidCertificates: _acceptInvalidCertificates).ConfigureAwait(true);

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

            // Surface the request/response details so the user can triage failures
            // (mis-rendered HTML pages, auth challenges, etc.) without leaving the
            // dialog. Only show the button when there's actually a transaction —
            // most exceptions short-circuit before any HTTP round-trip lands.
            _lastTestTransaction = result.Transaction;
            TestDetailsButton.Visibility = _lastTestTransaction is not null ? Visibility.Visible : Visibility.Collapsed;
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

    private void TestDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastTestTransaction is null)
        {
            return;
        }

        HttpDetailsWindow window = new(_lastTestTransaction)
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    private void SetStatus(string message, bool isError)
    {
        TestStatusText.Text = message;
        TestStatusText.Foreground = isError
            ? (Brush)Application.Current.FindResource("ErrorBrush")
            : (Brush)Application.Current.FindResource("TextSecondaryBrush");
        TestStatusText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Reads the field values, runs the same validation Save uses, and returns a
    /// populated DTO. On validation failure shows a MessageBox + focuses the bad
    /// field and returns false. Used by both Save and Test so a Test attempt is held
    /// to the same input rules as a Save.
    /// </summary>
    private bool TryBuildDtoFromFields(out ProxySettingDto? dto)
    {
        dto = null;

        string host = HostBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(host))
        {
            ShowValidation(Localizer.Instance["EditProxy_Validation_HostRequired"]);
            HostBox.Focus();
            return false;
        }

        if (!int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
            || port < 1
            || port > 65535)
        {
            ShowValidation(Localizer.Instance["EditProxy_Validation_PortInvalid"]);
            PortBox.Focus();
            PortBox.SelectAll();
            return false;
        }

        dto = new ProxySettingDto
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
        return true;
    }

    private void ShowValidation(string message)
        => MessageBox.Show(this, message, Localizer.Instance["Common_Error"], MessageBoxButton.OK, MessageBoxImage.Warning);
}
