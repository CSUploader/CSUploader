// <copyright file="Logger.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;
using CSUploader.Lib.Net.Http;

namespace CSUploader.Lib;

public delegate void LogEventHandler(object? sender, LogEvent e);

public class Logger : IAppLogger
{
    /// <summary>
    /// Static accessor for code that hasn't been migrated to DI yet.
    /// Prefer constructor injection of IAppLogger where possible.
    /// </summary>
    public static IAppLogger Current { get; set; } = new Logger();

    public event LogEventHandler? OnLogOutput;

    public void Log(
        object? sender,
        LogType logType,
        string text,
        HttpTransaction? httpTransaction = null,
        [CallerFilePath] string filePath = "",
        [CallerMemberName] string function = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        string fileName = Path.GetFileName(filePath);
        LogEvent logEvent = new()
        {
            LogType = logType,
            DateTime = DateTime.Now,
            Filename = fileName,
            Function = function,
            LineNumber = lineNumber,
            ThreadId = Environment.CurrentManagedThreadId,
            Message = text,
            HttpTransaction = httpTransaction,
        };

        OnLogOutput?.Invoke(sender, logEvent);
    }
}
