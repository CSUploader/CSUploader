// <copyright file="BindingErrorSink.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Logging;

namespace CSUploader.Tests.Avalonia;

/// <summary>
/// Captures Avalonia's binding-error log for the duration of a test, so a view can be asserted
/// QUIET and not merely functional.
/// <para>
/// A failed binding never throws and never changes what the user sees — it only prints to a
/// developer's debug output, which is exactly why one can sit in a view for months. The first of
/// these (LogsView's message tooltip, 2026-08-01) was reported from Visual Studio's output window,
/// not by anything failing.
/// </para>
/// </summary>
/// <example>
/// using BindingErrorSink sink = BindingErrorSink.Install();
/// … exercise the view …
/// Assert.Empty(sink.Errors);
/// </example>
internal sealed class BindingErrorSink : ILogSink, IDisposable
{
    private readonly ILogSink? _previous;

    private BindingErrorSink(ILogSink? previous) => _previous = previous;

    public List<string> Errors { get; } = [];

    /// <summary>Installs the sink as Avalonia's logger and restores the previous one on dispose.
    /// The headless session is process-global, so restoring matters.</summary>
    public static BindingErrorSink Install()
    {
        BindingErrorSink sink = new(Logger.Sink);
        Logger.Sink = sink;
        return sink;
    }

    public bool IsEnabled(LogEventLevel level, string area)
        => level >= LogEventLevel.Warning && area == LogArea.Binding;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (IsEnabled(level, area))
        {
            Errors.Add(messageTemplate);
        }
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (IsEnabled(level, area))
        {
            Errors.Add(messageTemplate + " | " + string.Join(", ", propertyValues));
        }
    }

    public void Dispose() => Logger.Sink = _previous;
}
