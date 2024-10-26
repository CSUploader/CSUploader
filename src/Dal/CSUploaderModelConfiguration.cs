// <copyright file="CSUploaderModelConfiguration.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Data.Entity;

namespace CSUploader.Dal
{
    public class CSUploaderModelConfiguration
    {
        public static void Configure(DbModelBuilder modelBuilder)
        {
            ConfigureFileHosterLoginEntity(modelBuilder);
            ConfigureSettingsEntity(modelBuilder);
        }

        private static void ConfigureFileHosterLoginEntity(DbModelBuilder modelBuilder)
        {
            modelBuilder.Entity<FileHosterLoginDbm>();
        }

        private static void ConfigureSettingsEntity(DbModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SettingDbm>();
        }
    }
}
