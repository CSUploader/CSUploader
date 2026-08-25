// <copyright file="IStartupUpdatePrompt.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Services;

/// <summary>
/// What the user chose when told at startup that an update is available.
/// </summary>
/// <param name="UpdateNow">
/// Whether to install it now. False covers every other way out — Later, Escape, Alt+F4, closing the
/// window — because none of those is consent to restart the app.
/// </param>
/// <param name="CheckAtStartup">
/// The checkbox as it stood when the window closed, whichever button was used. It is a preference,
/// not a consequence of the choice: unticking it and pressing Later has to be honoured, and an
/// opt-out dialog that only persists on the affirmative would drop exactly that.
/// </param>
public readonly record struct StartupUpdatePromptResult(bool UpdateNow, bool CheckAtStartup);

/// <summary>
/// Asks whether to install an update found during startup. Implemented by the head, because the
/// question is a window; called from initialization, which lives in Core.
/// </summary>
/// <remarks>
/// Must be called on the UI thread, after the real main window is visible — the prompt is owned by
/// it, and so is the progress window that follows an "Update now". A prompt owned by the splash
/// would be destroyed when the splash closes.
/// </remarks>
public interface IStartupUpdatePrompt
{
    /// <param name="newVersion">The version the check found.</param>
    /// <param name="currentVersion">The running version, for the "you have" line.</param>
    /// <param name="checkAtStartup">The stored preference, shown as the checkbox's initial state.</param>
    /// <param name="releaseNotesMarkdown">The release's notes as Markdown, or null when the package
    /// carries none — in which case the prompt shows no "what's new" section at all rather than an
    /// empty one.</param>
    Task<StartupUpdatePromptResult> ShowAsync(string newVersion, string currentVersion, bool checkAtStartup, string? releaseNotesMarkdown);
}
