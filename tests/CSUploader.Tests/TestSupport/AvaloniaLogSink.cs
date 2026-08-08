// <copyright file="AvaloniaLogSink.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Logging;

namespace CSUploader.Tests.Avalonia;

/// <summary>
/// Captures Avalonia's own warnings across <b>every</b> log area, not just bindings — the sibling of
/// <see cref="BindingErrorSink"/>, which is deliberately scoped to <see cref="LogArea.Binding"/>.
/// <para>
/// It exists because a dialog can be perfectly functional and still be shouting: the
/// <c>[Control] PlatformImpl is null, couldn't handle input</c> warning is emitted by the framework,
/// never surfaces in the UI, and only shows up in an IDE's output window — so nothing but a sink like
/// this can hold a view to being quiet.
/// </para>
/// </summary>
internal sealed class AvaloniaLogSink : ILogSink, IDisposable
{
    private readonly ILogSink? _previous;

    private AvaloniaLogSink(ILogSink? previous) => _previous = previous;

    public List<string> Messages { get; } = [];

    /// <summary>Installs the sink as Avalonia's logger and restores the previous one on dispose. The
    /// headless session is process-global, so restoring matters.</summary>
    public static AvaloniaLogSink Install()
    {
        AvaloniaLogSink sink = new(Logger.Sink);
        Logger.Sink = sink;
        return sink;
    }

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (IsEnabled(level, area))
        {
            Messages.Add($"[{area}] {messageTemplate}");
        }
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        => Log(level, area, source, messageTemplate);

    public void Dispose() => Logger.Sink = _previous;
}
