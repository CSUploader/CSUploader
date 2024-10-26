// <copyright file="Logger.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;

namespace CSUploader.Lib
{
    public delegate void LogEventHandler(object? sender, LogEvent e);

    public static class Logger
    {
        public static event LogEventHandler? OnLogOutput;

        public static void Log(
            object? sender,
            LogType logType,
            string text,
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
                Message = text
            };

            FireOnLogOutputEvent(sender, logEvent);
        }

        private static void FireOnLogOutputEvent(object? sender, LogEvent e)
        {
            OnLogOutput?.Invoke(sender, e);
        }
    }
}
