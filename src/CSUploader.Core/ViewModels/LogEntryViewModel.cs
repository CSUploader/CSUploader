// <copyright file="LogEntryViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;
using CSUploader.Lib.Net.Http;

namespace CSUploader.ViewModels;

public class LogEntryViewModel(LogEvent logEvent)
{
    public DateTime DateTime { get; } = logEvent.DateTime;

    public string? Filename { get; } = logEvent.Filename;

    public string? Function { get; } = logEvent.Function;

    public int LineNumber { get; } = logEvent.LineNumber;

    public string? Message { get; } = logEvent.Message;

    public int ThreadId { get; } = logEvent.ThreadId;

    public HttpTransaction? HttpTransaction { get; } = logEvent.HttpTransaction;

    public bool HasHttpTransaction => HttpTransaction is not null;

    /// <summary>HTTP response status code (e.g. 200, 401, 503), or null for non-HTTP rows.</summary>
    public int? StatusCode => HttpTransaction?.StatusCode;

    public string FullMessage => $"[{DateTime:HH:mm:ss.fff}] [{ThreadId}] {Filename}:{LineNumber} {Function} - {Message}";
}
