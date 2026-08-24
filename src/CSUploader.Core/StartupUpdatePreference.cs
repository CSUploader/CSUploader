// <copyright file="StartupUpdatePreference.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Upload;
using Microsoft.EntityFrameworkCore;

namespace CSUploader;

/// <summary>
/// Reads the "ask about updates when the app starts" preference early enough to decide <b>whether
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
    /// The saved preference, or null when none is stored or the store cannot be read.
    /// </summary>
    /// <remarks>
    /// Null is deliberately distinct from <see langword="false"/>. A first run, an un-migrated
    /// database or a locked file all mean "we do not know", and the caller treats not knowing as
    /// the default — which is to ask. Returning false on a read failure would silently disable a
    /// feature the user never turned off.
    /// </remarks>
    public static bool? ReadAskToUpdateAtStartup(IDbContextFactory<CSUploaderDbContext> factory)
    {
        try
        {
            using CSUploaderDbContext ctx = factory.CreateDbContext();

            string? value = ctx.Settings
                .AsNoTracking()
                .Where(s => s.Key == SettingKey.AskToUpdateAtStartup)
                .Select(s => s.Value)
                .FirstOrDefault();

            // Anything that is not recognisably a boolean is "we do not know" too, for exactly the
            // reason a missing row is: the caller turns not knowing into the default, and answering
            // false here would silently disable a feature nobody turned off.
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ? false : null;
        }
        catch (Exception)
        {
            // No database yet, an un-migrated one, or one another process holds. All of them mean
            // the same thing here: fall back to the default rather than not starting.
            return null;
        }
    }
}
