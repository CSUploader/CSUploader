// <copyright file="UpdateCheckOrigin.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.ViewModels;

/// <summary>
/// Why an update check is running. Three callers want three different things from a failure, and
/// encoding that as a flag on the call is what stops one of them from being handled as another.
/// </summary>
/// <remarks>
/// These do NOT form a ranking. Checks are shared, and a shared check owes every participant what
/// that participant was promised — a user joining a poll must not cancel the poll's toast, and a
/// poll joining a user's check must not add a toast to a dialog they are already reading. The
/// obligations accumulate; they do not compete.
/// </remarks>
public enum UpdateCheckOrigin
{
    /// <summary>
    /// A check the app started on its own at launch — either the one that gates startup behind the
    /// splash, or the quiet one that runs just after the window opens when the owner has turned
    /// "check for updates at startup" off.
    /// <para>
    /// SILENT on failure either way, for one reason that covers both: nobody asked for it. In the
    /// gated case there is nowhere to put a toast — the splash is on screen and the real window does
    /// not exist yet, so it would be orphaned or hidden behind it. In the ungated case the user
    /// specifically chose not to be interrupted at startup, and a failure notice is an interruption.
    /// The user finds out the ordinary way — the menu item stays disabled — and the periodic poll,
    /// which does surface failures, takes over.
    /// </para>
    /// </summary>
    Startup,

    /// <summary>
    /// The six-hourly poll. A failure surfaces once per episode as a toast, debounced so a machine
    /// that is offline for a week does not produce a toast every six hours.
    /// </summary>
    Periodic,

    /// <summary>
    /// Help → Check for Updates. The caller renders the outcome itself from the returned result, so
    /// nothing is surfaced from here — a toast as well would be a second answer to one question.
    /// </summary>
    User,
}
