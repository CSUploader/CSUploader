// <copyright file="IAppLogger.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.CompilerServices;

namespace CSUploader.Lib;

public interface IAppLogger
{
    event LogEventHandler? OnLogOutput;

    void Log(
        object? sender,
        LogType logType,
        string text,
        [CallerFilePath] string filePath = "",
        [CallerMemberName] string function = "",
        [CallerLineNumber] int lineNumber = 0);
}
