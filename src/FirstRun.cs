// <copyright file="FirstRun.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader;

public static class FirstRun
{
    public static void InitializeDatabase(IServiceProvider services, IAppLogger logger)
    {
        IDbContextFactory<CSUploaderDbContext> factory = services.GetRequiredService<IDbContextFactory<CSUploaderDbContext>>();
        using CSUploaderDbContext ctx = factory.CreateDbContext();
        ctx.Database.EnsureCreated();

        MigrateSchema(ctx, logger);

        logger.Log(null, LogType.Status, "Initialized database");
    }

    private static void MigrateSchema(CSUploaderDbContext ctx, IAppLogger logger)
    {
        // Drop orphan columns left over from removed features (e.g. compression).
        // Any column on UploadPackageFile not in the current model with NOT NULL would block inserts.
        (string Table, string Column)[] dropColumns =
        [
            ("UploadPackageFile", "CompressionPassword"),
            ("UploadPackageFile", "CompressionType"),
            ("UploadPackageFile", "CompressionMethod"),
            ("UploadPackageFile", "CompressionLevel"),
            ("UploadPackageFile", "CompressionSplitSize"),
            ("UploadPackageFile", "CompressionStatus"),
            ("UploadPackageFile", "IsCompressed"),
            ("UploadPackageFile", "CompressedFilePath"),
            ("UploadPackage", "CompressionPassword"),
            ("UploadPackage", "CompressionType"),
            ("UploadPackage", "CompressionMethod"),
            ("UploadPackage", "CompressionLevel"),
            ("UploadPackage", "CompressionSplitSize"),
            ("UploadPackage", "CompressionStatus"),
            // PackageFile.Priority retired: priority is per-package now (Package.Priority).
            ("UploadPackageFile", "Priority"),
        ];

        foreach ((string table, string column) in dropColumns)
        {
            if (!ColumnExists(ctx, table, column))
            {
                continue;
            }

            try
            {
                // Identifiers come from a hard-coded table, not user input. EF1002 is safe to suppress.
#pragma warning disable EF1002
                ctx.Database.ExecuteSqlRaw($"ALTER TABLE {table} DROP COLUMN {column}");
#pragma warning restore EF1002
                logger.Log(null, LogType.Status, $"Schema migration: dropped orphan column {table}.{column}");
            }
            catch (Exception ex)
            {
                logger.Log(null, LogType.Error, $"Schema migration failed to drop {table}.{column}: {ex.Message}");
            }
        }

        // EnsureCreated doesn't alter existing tables. Add missing columns for existing DBs.
        (string Table, string Column, string Sql)[] migrations =
        [
            ("UploadPackage", "CreatedDateTime", "ALTER TABLE UploadPackage ADD COLUMN CreatedDateTime TEXT NOT NULL DEFAULT '0001-01-01'"),
            ("UploadPackage", "ScheduledStartTime", "ALTER TABLE UploadPackage ADD COLUMN ScheduledStartTime TEXT"),
            ("UploadPackage", "IsCompleted", "ALTER TABLE UploadPackage ADD COLUMN IsCompleted INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackage", "SpeedLimitKBps", "ALTER TABLE UploadPackage ADD COLUMN SpeedLimitKBps INTEGER"),
            ("UploadPackage", "StartMode", "ALTER TABLE UploadPackage ADD COLUMN StartMode INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackageFile", "FileName", "ALTER TABLE UploadPackageFile ADD COLUMN FileName TEXT NOT NULL DEFAULT ''"),
            ("UploadPackageFile", "FileDirectory", "ALTER TABLE UploadPackageFile ADD COLUMN FileDirectory TEXT NOT NULL DEFAULT ''"),
            ("UploadPackageFile", "FileSize", "ALTER TABLE UploadPackageFile ADD COLUMN FileSize INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackageFile", "FileHoster", "ALTER TABLE UploadPackageFile ADD COLUMN FileHoster TEXT NOT NULL DEFAULT ''"),
            ("UploadPackageFile", "FileHosterName", "ALTER TABLE UploadPackageFile ADD COLUMN FileHosterName TEXT NOT NULL DEFAULT ''"),
            ("UploadPackageFile", "StartDateTime", "ALTER TABLE UploadPackageFile ADD COLUMN StartDateTime TEXT NOT NULL DEFAULT '0001-01-01'"),
            ("UploadPackageFile", "FinishedDateTime", "ALTER TABLE UploadPackageFile ADD COLUMN FinishedDateTime TEXT NOT NULL DEFAULT '0001-01-01'"),
            ("UploadPackageFile", "FileUrl", "ALTER TABLE UploadPackageFile ADD COLUMN FileUrl TEXT NOT NULL DEFAULT ''"),
            ("UploadPackageFile", "State", "ALTER TABLE UploadPackageFile ADD COLUMN State INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackageFile", "Error", "ALTER TABLE UploadPackageFile ADD COLUMN Error TEXT"),
            ("UploadPackageFile", "IsHashingComplete", "ALTER TABLE UploadPackageFile ADD COLUMN IsHashingComplete INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackageFile", "FileHosterLoginId", "ALTER TABLE UploadPackageFile ADD COLUMN FileHosterLoginId INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackageFile", "SortOrder", "ALTER TABLE UploadPackageFile ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackageFile", "PackageId", "ALTER TABLE UploadPackageFile ADD COLUMN PackageId INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackageFile", "IsHidden", "ALTER TABLE UploadPackageFile ADD COLUMN IsHidden INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackageFile", "IsRemovedFromUploads", "ALTER TABLE UploadPackageFile ADD COLUMN IsRemovedFromUploads INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackage", "IsRemovedFromUploads", "ALTER TABLE UploadPackage ADD COLUMN IsRemovedFromUploads INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackageFile", "FileHash", "ALTER TABLE UploadPackageFile ADD COLUMN FileHash TEXT"),
            ("UploadPackage", "Priority", "ALTER TABLE UploadPackage ADD COLUMN Priority INTEGER NOT NULL DEFAULT 0"),
            // Session-cookie cache for captcha-gated hosters (ex-load.com). Nullable on
            // existing DBs so accounts on POST-login hosters carry NULLs without breaking.
            ("FileHosterLogin", "SessionCookie", "ALTER TABLE FileHosterLogin ADD COLUMN SessionCookie TEXT"),
            ("FileHosterLogin", "SessionCookieExpiresUtc", "ALTER TABLE FileHosterLogin ADD COLUMN SessionCookieExpiresUtc TEXT"),
            // Pinned proxy per captcha-gated account. Nullable so non-pinned accounts on
            // existing DBs carry NULL. See FileHosterLoginDbm.PinnedProxyId for semantics.
            ("FileHosterLogin", "PinnedProxyId", "ALTER TABLE FileHosterLogin ADD COLUMN PinnedProxyId INTEGER"),
            // API key for key-based REST APIs (Ex-Load). Either supplied directly by the
            // user, OR auto-derived from a username/password sign-in plus a my_account scrape.
            ("FileHosterLogin", "ApiKey", "ALTER TABLE FileHosterLogin ADD COLUMN ApiKey TEXT"),
            // Storage quota tracking. Populated by pipelines whose API exposes usage stats
            // (FileBoom's /v1/users/me/statistic). Nullable so non-quota-aware hosters
            // carry NULL.
            ("FileHosterLogin", "StorageUsedBytes", "ALTER TABLE FileHosterLogin ADD COLUMN StorageUsedBytes INTEGER"),
            ("FileHosterLogin", "StorageQuotaBytes", "ALTER TABLE FileHosterLogin ADD COLUMN StorageQuotaBytes INTEGER"),
            // Wall-clock of the last verifier round-trip (success OR failure). Drives the
            // Account Manager's "Refreshed at" column. TEXT (ISO-8601) matches the existing
            // DateTime column convention on this table (SessionCookieExpiresUtc).
            ("FileHosterLogin", "LastRefreshedDateTime", "ALTER TABLE FileHosterLogin ADD COLUMN LastRefreshedDateTime TEXT"),
        ];

        foreach ((string table, string column, string sql) in migrations)
        {
            if (ColumnExists(ctx, table, column))
            {
                continue;
            }

            try
            {
                ctx.Database.ExecuteSqlRaw(sql);
                logger.Log(null, LogType.Status, $"Schema migration: added {table}.{column}");
            }
            catch (Exception ex)
            {
                logger.Log(null, LogType.Error, $"Schema migration failed for {table}.{column}: {ex.Message}");
            }
        }

        // Data migrations — rename persisted hoster identities when a display name changes.
        // "ExLoad" → "Ex-Load" (2026-06-10). Idempotent: UPDATE … WHERE name = old affects
        // zero rows on subsequent startups, so it's safe to run every launch. Covers the
        // account row (FileHosterLogin) and any upload history (UploadPackageFile) so the
        // renamed hoster still resolves through the registry and shows the new name.
        (string Table, string Column, string OldValue, string NewValue)[] renames =
        [
            ("FileHosterLogin", "FileHosterName", "ExLoad", "Ex-Load"),
            ("UploadPackageFile", "FileHosterName", "ExLoad", "Ex-Load"),
            ("UploadPackageFile", "FileHoster", "ExLoad", "Ex-Load"),
        ];

        foreach ((string table, string column, string oldValue, string newValue) in renames)
        {
            if (!ColumnExists(ctx, table, column))
            {
                continue;
            }

            try
            {
                // Table/column come from the hard-coded `renames` array, not user input;
                // the VALUES are passed as parameters ({0}/{1}) so they're injection-safe.
#pragma warning disable EF1002
                int affected = ctx.Database.ExecuteSqlRaw(
                    $"UPDATE {table} SET {column} = {{0}} WHERE {column} = {{1}}", newValue, oldValue);
#pragma warning restore EF1002
                if (affected > 0)
                {
                    logger.Log(null, LogType.Status, $"Data migration: renamed {affected} {table}.{column} '{oldValue}' → '{newValue}'");
                }
            }
            catch (Exception ex)
            {
                logger.Log(null, LogType.Error, $"Data migration failed for {table}.{column}: {ex.Message}");
            }
        }

        // New tables added after the first release — EnsureCreated() doesn't add them
        // to existing databases, so create them explicitly when missing.
        if (!TableExists(ctx, "LogEntry"))
        {
            try
            {
#pragma warning disable EF1002
                ctx.Database.ExecuteSqlRaw(@"
                    CREATE TABLE LogEntry (
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        DateTime TEXT NOT NULL,
                        LogType INTEGER NOT NULL,
                        Filename TEXT,
                        Function TEXT,
                        LineNumber INTEGER NOT NULL DEFAULT 0,
                        ThreadId INTEGER NOT NULL DEFAULT 0,
                        Message TEXT NOT NULL DEFAULT ''
                    )");
                ctx.Database.ExecuteSqlRaw("CREATE INDEX IX_LogEntry_DateTime ON LogEntry (DateTime)");
#pragma warning restore EF1002
                logger.Log(null, LogType.Status, "Schema migration: created table LogEntry");
            }
            catch (Exception ex)
            {
                logger.Log(null, LogType.Error, $"Schema migration failed to create LogEntry: {ex.Message}");
            }
        }

        if (!TableExists(ctx, "ProxySetting"))
        {
            try
            {
#pragma warning disable EF1002
                // ProblemsCount column was retired with the green-check / red-X test
                // indicator; new databases skip it and existing rows simply ignore the
                // legacy column (EF reads only the mapped properties).
                ctx.Database.ExecuteSqlRaw(@"
                    CREATE TABLE ProxySetting (
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        Type INTEGER NOT NULL DEFAULT 0,
                        Host TEXT NOT NULL DEFAULT '',
                        Port INTEGER NOT NULL DEFAULT 0,
                        Username TEXT,
                        Password TEXT,
                        Enabled INTEGER NOT NULL DEFAULT 1,
                        Priority INTEGER NOT NULL DEFAULT 0
                    )");
#pragma warning restore EF1002
                logger.Log(null, LogType.Status, "Schema migration: created table ProxySetting");
            }
            catch (Exception ex)
            {
                logger.Log(null, LogType.Error, $"Schema migration failed to create ProxySetting: {ex.Message}");
            }
        }
    }

    private static bool TableExists(CSUploaderDbContext ctx, string table)
    {
        using Microsoft.Data.Sqlite.SqliteCommand cmd = new(
            $"SELECT name FROM sqlite_master WHERE type='table' AND name='{table}'",
            (Microsoft.Data.Sqlite.SqliteConnection)ctx.Database.GetDbConnection());
        ctx.Database.OpenConnection();
        using Microsoft.Data.Sqlite.SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read();
    }

    private static bool ColumnExists(CSUploaderDbContext ctx, string table, string column)
    {
        using Microsoft.Data.Sqlite.SqliteCommand cmd = new($"PRAGMA table_info({table})", (Microsoft.Data.Sqlite.SqliteConnection)ctx.Database.GetDbConnection());
        ctx.Database.OpenConnection();
        using Microsoft.Data.Sqlite.SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
