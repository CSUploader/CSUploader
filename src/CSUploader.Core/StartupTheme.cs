// <copyright file="StartupTheme.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Upload;
using Microsoft.EntityFrameworkCore;

namespace CSUploader;

/// <summary>
/// Reads the persisted theme preference early enough to apply it <b>before the first window is
/// shown</b>.
/// <para>
/// <see cref="ViewModels.MainViewModel.InitializeAsync"/> also restores this setting, and its comment
/// there says it does so "before the user sees the UI to avoid a light-&gt;dark flash" — but it runs
/// from <c>MainWindow.Opened</c>, which fires once the window is already on screen, and only after a
/// database init and a log-table hydration of up to 5000 rows. So the intent was right and the timing
/// could not deliver it: a dark-theme user watched the shell paint light and then flip.
/// </para>
/// <para>
/// This is deliberately a <b>synchronous, single-column, failure-tolerant</b> read: it happens on the
/// UI thread before anything is drawn, so it must be cheap and must never be able to stop the app
/// starting. Anything unexpected — no database yet (first run), a schema not migrated yet, a locked
/// file — returns null, which leaves the caller on its default theme and lets the normal hydration
/// path correct it exactly as it did before. The cost of that case is the flash that used to happen
/// every time.
/// </para>
/// </summary>
public static class StartupTheme
{
    /// <summary>
    /// The saved dark-mode preference, or null when none is stored or the store can't be read.
    /// </summary>
    /// <remarks>
    /// Only <c>Key</c> and <c>Value</c> are touched, rather than materialising the entity: this runs
    /// before any schema migration has, and a column added to <see cref="SettingDbm"/> but not yet
    /// present in an older database would otherwise throw here rather than in the migration that
    /// exists to fix it.
    /// </remarks>
    public static bool? ReadPersistedDarkMode(IDbContextFactory<CSUploaderDbContext> factory)
    {
        try
        {
            using CSUploaderDbContext ctx = factory.CreateDbContext();

            string? value = ctx.Settings
                .AsNoTracking()
                .Where(s => s.Key == SettingKey.IsDarkMode)
                .Select(s => s.Value)
                .FirstOrDefault();

            return value is null ? null : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // No database yet, an un-migrated one, or one another process holds. All of them mean the
            // same thing here: start on the default theme rather than not starting.
            return null;
        }
    }
}
