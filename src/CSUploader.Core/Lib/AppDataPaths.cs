// <copyright file="AppDataPaths.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib;

/// <summary>
/// Resolves the writable location for the app's SQLite database.
/// <para>
/// Windows keeps the shipped v1.0.0 convention — the database sits beside the executable (the
/// Velopack per-user app directory, which is writable) — so existing users' data is untouched.
/// A packaged non-Windows app (Linux/macOS AppImage) instead runs from a READ-ONLY squashfs mount
/// (or an ephemeral extract-and-run temp dir), where SQLite cannot create the file and startup dies
/// with "SQLite Error 14: unable to open database file". There the database goes into the per-user
/// data directory — the same root CEF's cache uses (see the head's CefBootstrap) — which the caller
/// creates before opening the connection.
/// </para>
/// </summary>
public static class AppDataPaths
{
    /// <summary>Per-user data sub-folder name (also the CEF cache root's parent).</summary>
    public const string AppFolderName = "CSUploader";

    /// <summary>SQLite database file name.</summary>
    public const string DbFileName = "CSUploader.db";

    /// <summary>
    /// Composes the database path. Pure (no I/O) so both platform branches are unit-testable on any
    /// host. Windows: <paramref name="baseDirectory"/>\CSUploader.db. Non-Windows:
    /// <paramref name="localAppData"/>/CSUploader/CSUploader.db.
    /// </summary>
    public static string ComposeDbPath(bool isWindows, string baseDirectory, string localAppData)
        => isWindows
            ? Path.Combine(baseDirectory, DbFileName)
            : Path.Combine(localAppData, AppFolderName, DbFileName);

    /// <summary>
    /// Resolves the per-user local-app-data root robustly. <see cref="Environment.SpecialFolder.LocalApplicationData"/>
    /// returns an EMPTY string on Unix when ~/.local/share does not yet exist (documented .NET behavior),
    /// so fall back to $XDG_DATA_HOME, then $HOME/.local/share, then beside the executable. Windows always
    /// returns a valid path, so this only affects non-Windows. Mirrors the head's CefBootstrap resolver.
    /// </summary>
    public static string ResolveLocalAppData()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(appData) && Path.IsPathRooted(appData))
        {
            return appData;
        }

        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg))
        {
            return xdg;
        }

        string home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && Path.IsPathRooted(home)
            ? Path.Combine(home, ".local", "share")
            : AppContext.BaseDirectory;
    }
}
