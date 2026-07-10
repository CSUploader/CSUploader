// <copyright file="WpfUpdateProgressSink.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Windows;
using CSUploader.Views;

namespace CSUploader.Services;

/// <summary>
/// WPF implementation of <see cref="IUpdateProgressSink"/>. Owns the non-modal
/// <see cref="UpdateProgressWindow"/>: <see cref="Open"/> shows it (owner resolution moved here
/// verbatim from MainViewModel), and SetStatus/Report drive it. The install flow never calls
/// <see cref="Close"/> — see the interface remarks — so it is here only for interface completeness.
/// </summary>
public sealed class WpfUpdateProgressSink : IUpdateProgressSink
{
    private UpdateProgressWindow? _window;

    public void Open()
    {
        _window = new UpdateProgressWindow
        {
            Owner = Application.Current?.MainWindow,
        };
        _window.Show();
    }

    public void SetStatus(string status) => _window?.SetStatus(status);

    public void Report(int percent) => _window?.SetProgress(percent);

    public void Close() => _window?.Close();
}
