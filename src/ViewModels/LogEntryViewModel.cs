// <copyright file="LogEntryViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;

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
    }

    public DateTime DateTime { get; }

    public string? Filename { get; }

    public string? Function { get; }

    public int LineNumber { get; }

    public string? Message { get; }

    public int ThreadId { get; }

    public string FullMessage => $"[{DateTime:HH:mm:ss.fff}] [{ThreadId}] {Filename}:{LineNumber} {Function} - {Message}";
}
