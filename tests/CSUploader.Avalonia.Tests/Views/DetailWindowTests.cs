// <copyright file="DetailWindowTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net.Http;
using CSUploader.ViewModels;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless tests for the two Phase 4 Task 7 detail windows (<see cref="HttpDetailsWindow"/>,
/// <see cref="LogDetailsWindow"/>) — the nested-TabControl carriers. The load-bearing checks are that the
/// code-behind's pure formatting against a Core <see cref="HttpTransaction"/> lands in the summary/timing/
/// proxy header and all eight sub-tab boxes (a known substring per box), the null-transaction path shows the
/// <c>HttpDetails_NoData</c> fallback, and that LogDetails' OneWay bindings render the converted DateTime,
/// the thread id, and the multi-line message. Every shown window is closed in a <c>finally</c> (headless
/// windows are process-global for the session).
/// </summary>
public class DetailWindowTests
{
    // ── HttpDetailsWindow: transaction populates every box ──

    [AvaloniaFact]
    public void HttpDetails_Transaction_PopulatesHeaderAndAllEightBoxes()
    {
        var dlg = new HttpDetailsWindow(SynthTransaction());
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            // Summary header (Summary/Timing/Proxy).
            Assert.Contains("POST", dlg.SummaryText.Text, StringComparison.Ordinal);
            Assert.Contains("200", dlg.SummaryText.Text, StringComparison.Ordinal);
            Assert.Contains("14:30:45.000", dlg.TimingText.Text, StringComparison.Ordinal); // StartTime HH:mm:ss.fff
            Assert.Contains("842ms", dlg.TimingText.Text, StringComparison.Ordinal);        // Duration from Start/EndTime
            Assert.Contains("http://127.0.0.1:8080", dlg.ProxyText.Text, StringComparison.Ordinal);

            // Request sub-tabs.
            Assert.Contains("POST https://api.example-hoster.com/v1/upload HTTP/1.1", dlg.RequestHeadersBox.Text, StringComparison.Ordinal);
            Assert.Contains("Authorization", dlg.RequestHeadersBox.Text, StringComparison.Ordinal); // a request header name
            Assert.Contains("movie.mkv", dlg.RequestBodyRawBox.Text, StringComparison.Ordinal);     // raw body
            Assert.Contains("  \"name\"", dlg.RequestBodyJsonBox.Text, StringComparison.Ordinal);   // pretty-printed 2-space indent
            Assert.Contains("00000000", dlg.RequestHexBox.Text, StringComparison.Ordinal);          // hex-dump offset column

            // Response sub-tabs.
            Assert.Contains("HTTP/1.1 200 OK", dlg.ResponseHeadersBox.Text, StringComparison.Ordinal);
            Assert.Contains("Server", dlg.ResponseHeadersBox.Text, StringComparison.Ordinal);       // a response header name
            Assert.Contains("fileId", dlg.ResponseBodyRawBox.Text, StringComparison.Ordinal);       // raw body
            Assert.Contains("  \"status\"", dlg.ResponseBodyJsonBox.Text, StringComparison.Ordinal); // pretty-printed indent
            Assert.Contains("00000000", dlg.ResponseHexBox.Text, StringComparison.Ordinal);         // hex-dump offset column

            // Full dump stitches both header blocks under the REQUEST/RESPONSE dividers.
            Assert.Contains("REQUEST", dlg.FullDumpBox.Text, StringComparison.Ordinal);
            Assert.Contains("RESPONSE", dlg.FullDumpBox.Text, StringComparison.Ordinal);
            Assert.Contains("Authorization", dlg.FullDumpBox.Text, StringComparison.Ordinal);
            Assert.Contains("HTTP/1.1 200 OK", dlg.FullDumpBox.Text, StringComparison.Ordinal);
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void HttpDetails_NullTransaction_ShowsNoDataFallback()
    {
        // The parameterless (loader) ctor routes to the private ctor's null branch.
        var dlg = new HttpDetailsWindow();
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Localizer.Instance["HttpDetails_NoData"], dlg.SummaryText.Text);
            // No transaction → the timing/proxy lines are never set.
            Assert.True(string.IsNullOrEmpty(dlg.TimingText.Text));
            Assert.True(string.IsNullOrEmpty(dlg.ProxyText.Text));
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void HttpDetails_LogEntryWithoutTransaction_FallsBackToMessage()
    {
        // The LogsView ctor path: an entry with no HTTP transaction falls back to the entry's own message.
        var entry = new LogEntryViewModel(new LogEvent { Message = "plain log line, no transaction" });
        var dlg = new HttpDetailsWindow(entry);
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("plain log line, no transaction", dlg.SummaryText.Text);
        }
        finally
        {
            dlg.Close();
        }
    }

    // ── LogDetailsWindow: OneWay bindings render ──

    [AvaloniaFact]
    public void LogDetails_Fields_BindThroughViewModel()
    {
        var dlg = new LogDetailsWindow(new LogEntryViewModel(SynthLogEvent()));
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            // DateTime is routed through DateTimeFormatConverter (yyyy/MM/dd HH:mm:ss, invariant).
            Assert.Equal("2026/07/12 14:30:45", dlg.DateTimeBox.Text);
            Assert.Equal("7", dlg.ThreadIdBox.Text); // int → string default conversion

            // The multi-line message lands in the Text tab box.
            Assert.Contains("Upload failed after 3 retries.", dlg.MessageTextBox.Text, StringComparison.Ordinal);
            Assert.Contains("\n", dlg.MessageTextBox.Text, StringComparison.Ordinal); // preserved as multi-line
        }
        finally
        {
            dlg.Close();
        }
    }

    // ── synthesized data (mirrors the WPF reference driver + the Avalonia gallery factories) ──

    private static LogEvent SynthLogEvent() => new()
    {
        LogType = LogType.Error,
        DateTime = new DateTime(2026, 7, 12, 14, 30, 45, 123),
        ThreadId = 7,
        Filename = "FileHosterClient.cs",
        Function = "UploadAsync",
        LineNumber = 214,
        Message = "Upload failed after 3 retries.\n"
            + "HTTP 503 Service Unavailable\n"
            + "The origin server is temporarily unable to service the request.",
    };

    private static HttpTransaction SynthTransaction()
    {
        DateTime start = new(2026, 7, 12, 14, 30, 45);
        return new HttpTransaction
        {
            Method = "POST",
            Url = "https://api.example-hoster.com/v1/upload",
            Proxy = "http://127.0.0.1:8080",
            StatusCode = 200,
            StatusReason = "OK",
            StartTime = start,
            EndTime = start.AddMilliseconds(842),
            RequestHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json"],
                ["Authorization"] = ["Bearer synthesized-session-token"],
                ["User-Agent"] = ["CSUploader/0.0.6"],
            },
            RequestBody = "{\"name\":\"movie.mkv\",\"size\":5242880,\"folderId\":0}",
            ResponseHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json"],
                ["Server"] = ["nginx"],
            },
            ResponseBody = "{\"status\":\"ok\",\"fileId\":\"abc123\",\"url\":\"https://example-hoster.com/f/abc123\"}",
            ResponseBodyBytes = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"fileId\":\"abc123\"}"),
        };
    }
}
