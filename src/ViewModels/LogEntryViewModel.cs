// <copyright file="LogEntryViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.ViewModels;

public class LogEntryViewModel
{
    public LogEntryViewModel(LogEvent logEvent)
    {
        DateTime = logEvent.DateTime;
        Filename = logEvent.Filename;
        Function = logEvent.Function;
        LineNumber = logEvent.LineNumber;
        Message = logEvent.Message;
        ThreadId = logEvent.ThreadId;
        HttpTransaction = logEvent.HttpTransaction;
    }

    public DateTime DateTime { get; }

    public string? Filename { get; }

    public string? Function { get; }

    public int LineNumber { get; }

    public string? Message { get; }

    public int ThreadId { get; }

    public HttpTransaction? HttpTransaction { get; }

    public bool HasHttpTransaction => HttpTransaction is not null;

    public string FullMessage => $"[{DateTime:HH:mm:ss.fff}] [{ThreadId}] {Filename}:{LineNumber} {Function} - {Message}";
}
