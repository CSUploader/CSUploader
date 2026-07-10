// <copyright file="IUiShell.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Services;

/// <summary>
/// Abstraction over top-level application-shell operations that ViewModels occasionally
/// need — bringing the main window to the foreground and shutting the app down. The WPF
/// head wraps <c>Application.Current.MainWindow</c> / <c>Application.Current.Shutdown</c>;
/// the Avalonia head supplies its own.
/// </summary>
public interface IUiShell
{
    /// <summary>Restores and brings the main window to the foreground.</summary>
    void ActivateMainWindow();

    /// <summary>Shuts the application down.</summary>
    void Shutdown();
}
