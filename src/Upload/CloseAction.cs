// <copyright file="CloseAction.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>
/// What the main window's X (close) button should do. <see cref="Ask"/> is the first-run
/// state — the next click prompts the user to pick a behaviour, optionally persisting it.
/// </summary>
public enum CloseAction
{
    /// <summary>Prompt the user to pick a behaviour next time the X button is clicked.</summary>
    Ask,

    /// <summary>Hide the window into the system tray; clicking the tray icon restores it.</summary>
    MinimizeToTray,

    /// <summary>Quit the application normally.</summary>
    Exit,
}
