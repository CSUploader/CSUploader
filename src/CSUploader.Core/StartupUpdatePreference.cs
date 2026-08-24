// <copyright file="StartupUpdatePreference.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Upload;
using Microsoft.EntityFrameworkCore;

namespace CSUploader;

/// <summary>
/// Reads the "check for updates at startup" preference early enough to decide <b>whether
/// to show a splash at all</b>, which is before any window exists.
/// <para>
/// It cannot come from <see cref="AppSettings"/>: that object holds nothing but defaults until
/// <c>SettingsViewModel.LoadAsync</c> hydrates it, which happens well after the first window is on
/// screen. Reading it there would mean every user got the splash on every launch regardless of what
/// they had chosen — the setting would appear to do nothing.
/// </para>
/// </summary>
/// <remarks>
/// The same shape as <see cref="StartupTheme"/>, and for the same reasons: a synchronous,
/// single-column, failure-tolerant read on the UI thread before anything is drawn. Only
/// <c>Key</c> and <c>Value</c> are touched rather than materialising the entity, because this runs
/// before any schema migration has, and a column added to <see cref="SettingDbm"/> but not yet
/// present in an older database would otherwise throw here rather than in the migration that exists
/// to fix it.
/// </remarks>
public static class StartupUpdatePreference
{
    /// <summary>
    /// Interprets a stored value, or answers null for one that is not recognisably a boolean.
    /// </summary>
    /// <remarks>
    /// <b>Shared with settings hydration deliberately.</b> This decision is made twice — here,
    /// before any window exists, and again when <c>SettingsViewModel</c> hydrates. The two
    /// disagreeing costs a user who turned startup checks off a splash, and the request moved in
    /// FRONT of the window instead of quietly behind it — on every launch, with Settings showing the
    /// feature as off and nothing ever repairing it. Not an extra request: off still checks, just
    /// later. And no longer a PROMPT either, since the gated path re-reads the hydrated value before
    /// asking or installing. But two parsers that merely look equivalent can drift on a detail like
    /// whitespace, so there is one.
    /// </remarks>
    public static bool? Parse(string? value)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    /// <summary>
    /// The saved preference, or null when none is stored or the store cannot be read.
    /// </summary>
    /// <remarks>
    /// Null is deliberately distinct from <see langword="false"/>. A first run, an un-migrated
    /// database or a locked file all mean "we do not know", and the caller treats not knowing as
    /// the default — which is to check. Returning false on a read failure would silently disable a
    /// feature the user never turned off.
    /// </remarks>
    public static bool? ReadCheckForUpdatesAtStartup(IDbContextFactory<CSUploaderDbContext> factory)
    {
        try
        {
            using CSUploaderDbContext ctx = factory.CreateDbContext();

            string? value = ctx.Settings
                .AsNoTracking()
                .Where(s => s.Key == SettingKey.CheckForUpdatesAtStartup)
                .Select(s => s.Value)
                .FirstOrDefault();

            return Parse(value);
        }
        catch (Exception)
        {
            // No database yet, an un-migrated one, or one another process holds. All of them mean
            // the same thing here: fall back to the default rather than not starting.
            return null;
        }
    }
}
