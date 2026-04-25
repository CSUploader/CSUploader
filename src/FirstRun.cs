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
            ("UploadPackage", "DirectoryPath", "ALTER TABLE UploadPackage ADD COLUMN DirectoryPath TEXT NOT NULL DEFAULT ''"),
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
            ("UploadPackageFile", "Priority", "ALTER TABLE UploadPackageFile ADD COLUMN Priority INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackageFile", "SortOrder", "ALTER TABLE UploadPackageFile ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackageFile", "PackageId", "ALTER TABLE UploadPackageFile ADD COLUMN PackageId INTEGER NOT NULL DEFAULT 0"),
            ("UploadPackageFile", "IsHidden", "ALTER TABLE UploadPackageFile ADD COLUMN IsHidden INTEGER NOT NULL DEFAULT 0"),
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
