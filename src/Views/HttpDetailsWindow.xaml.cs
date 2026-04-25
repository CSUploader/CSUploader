// <copyright file="HttpDetailsWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text;
using System.Windows;
using CSUploader.Lib.Net.Http;
using CSUploader.ViewModels;

namespace CSUploader.Views;

public partial class HttpDetailsWindow : Window
{
    public HttpDetailsWindow(LogEntryViewModel entry)
    {
        InitializeComponent();

        HttpTransaction? tx = entry.HttpTransaction;
        if (tx is null)
        {
            SummaryText.Text = entry.Message ?? "(no data)";
            return;
        }

        // Summary header
        SummaryText.Text = tx.Summary;
        TimingText.Text = $"Started: {tx.StartTime:HH:mm:ss.fff}  |  Duration: {tx.Duration.TotalMilliseconds:F0}ms  |  Size: {tx.ResponseBodyBytes?.Length ?? 0} bytes";

        // Request
        RequestHeadersBox.Text = tx.RequestHeadersText;
        RequestBodyRawBox.Text = tx.RequestBody ?? "(no body)";
        RequestBodyJsonBox.Text = HttpTransaction.PrettyPrintJson(tx.RequestBody);
        RequestHexBox.Text = tx.RequestBodyBytes is not null
            ? HttpTransaction.ToHexDump(tx.RequestBodyBytes)
            : HttpTransaction.ToHexDump(tx.RequestBody is not null ? Encoding.UTF8.GetBytes(tx.RequestBody) : null);

        // Response
        ResponseHeadersBox.Text = tx.ResponseHeadersText;
        ResponseBodyRawBox.Text = tx.ResponseBody ?? "(no body)";
        ResponseBodyJsonBox.Text = HttpTransaction.PrettyPrintJson(tx.ResponseBody);
        ResponseHexBox.Text = HttpTransaction.ToHexDump(tx.ResponseBodyBytes);

        // Full dump
        StringBuilder dump = new();
        dump.AppendLine("══════════════════ REQUEST ══════════════════");
        dump.AppendLine();
        dump.AppendLine(tx.RequestHeadersText);
        if (!string.IsNullOrEmpty(tx.RequestBody))
        {
            dump.AppendLine();
            dump.AppendLine(HttpTransaction.PrettyPrintJson(tx.RequestBody));
        }

        dump.AppendLine();
        dump.AppendLine("══════════════════ RESPONSE ══════════════════");
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
