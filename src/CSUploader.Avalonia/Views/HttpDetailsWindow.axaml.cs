// <copyright file="HttpDetailsWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using System.Text;
using Avalonia.Controls;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net.Http;
using CSUploader.ViewModels;

namespace CSUploader.Views;

/// <summary>
/// Inspector for one captured <see cref="HttpTransaction"/> (port of the WPF <c>HttpDetailsWindow</c>): a
/// summary header over Request / Response / Full Dump tabs, the first two nesting Headers / Body raw /
/// Body JSON / Hex sub-tabs. The code-behind is pure formatting against the Core transaction — the string
/// building, <see cref="HttpTransaction.PrettyPrintJson"/> / <see cref="HttpTransaction.ToHexDump"/> calls,
/// and the Localizer formats are framework-free, so it ports near-verbatim from WPF.
/// </summary>
public partial class HttpDetailsWindow : Window
{
    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001); routes to the fallback
    // "no data" render. The app always uses one of the transaction/entry overloads.
    public HttpDetailsWindow()
        : this(tx: null, fallbackMessage: null)
    {
    }

    public HttpDetailsWindow(LogEntryViewModel entry)
        : this(entry.HttpTransaction, entry.Message)
    {
    }

    public HttpDetailsWindow(HttpTransaction transaction)
        : this(transaction, fallbackMessage: null)
    {
    }

    private HttpDetailsWindow(HttpTransaction? tx, string? fallbackMessage)
    {
        InitializeComponent();

        if (tx is null)
        {
            SummaryText.Text = fallbackMessage ?? Localizer.Instance["HttpDetails_NoData"];
            return;
        }

        // Summary header
        SummaryText.Text = tx.Summary;
        TimingText.Text = string.Format(
            CultureInfo.CurrentCulture,
            Localizer.Instance["HttpDetails_Timing_Format"],
            tx.StartTime.ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture),
            tx.Duration.TotalMilliseconds.ToString("F0", CultureInfo.CurrentCulture),
            tx.ResponseBodyBytes?.Length ?? 0);
        ProxyText.Text = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["HttpDetails_Proxy_Format"], tx.Proxy);

        string noBody = Localizer.Instance["HttpDetails_NoBody"];

        // Request
        RequestHeadersBox.Text = tx.RequestHeadersText;
        RequestBodyRawBox.Text = tx.RequestBody ?? noBody;
        RequestBodyJsonBox.Text = HttpTransaction.PrettyPrintJson(tx.RequestBody);
        RequestHexBox.Text = tx.RequestBodyBytes is not null
            ? HttpTransaction.ToHexDump(tx.RequestBodyBytes)
            : HttpTransaction.ToHexDump(tx.RequestBody is not null ? Encoding.UTF8.GetBytes(tx.RequestBody) : null);

        // Response
        ResponseHeadersBox.Text = tx.ResponseHeadersText;
        ResponseBodyRawBox.Text = tx.ResponseBody ?? noBody;
        ResponseBodyJsonBox.Text = HttpTransaction.PrettyPrintJson(tx.ResponseBody);
        ResponseHexBox.Text = HttpTransaction.ToHexDump(tx.ResponseBodyBytes);

        // Full dump
        string reqHeader = Localizer.Instance["HttpDetails_FullDump_Request"];
        string respHeader = Localizer.Instance["HttpDetails_FullDump_Response"];
        StringBuilder dump = new();
        dump.AppendLine($"══════════════════ {reqHeader} ══════════════════");
        dump.AppendLine();
        dump.AppendLine(tx.RequestHeadersText);
        if (!string.IsNullOrEmpty(tx.RequestBody))
        {
            dump.AppendLine();
            dump.AppendLine(HttpTransaction.PrettyPrintJson(tx.RequestBody));
        }

        dump.AppendLine();
        dump.AppendLine($"══════════════════ {respHeader} ══════════════════");
        dump.AppendLine();
        dump.AppendLine(tx.ResponseHeadersText);
        if (!string.IsNullOrEmpty(tx.ResponseBody))
        {
            dump.AppendLine();
            dump.AppendLine(HttpTransaction.PrettyPrintJson(tx.ResponseBody));
        }

        FullDumpBox.Text = dump.ToString();
    }
}
