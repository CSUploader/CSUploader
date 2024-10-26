// <copyright file="CSUploaderDbContext.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Data.Common;
using System.Data.Entity;
using System.Diagnostics.CodeAnalysis;

namespace CSUploader.Dal
{
    public class CSUploaderDbContext : DbContext
    {
        public CSUploaderDbContext()
            : this("CSUploaderDb")
        {
            Configure();
        }

        public CSUploaderDbContext(string nameOrConnectionString)
            : base(nameOrConnectionString)
        {
            Configure();
        }

        public CSUploaderDbContext(DbConnection connection, bool contextOwnsConnection)
            : base(connection, contextOwnsConnection)
        {
            Configure();
        }

        [NotNull]
        public virtual DbSet<SettingDbm>? Settings { get; set; }

        [NotNull]
        public virtual DbSet<FileHosterLoginDbm>? FileHosterLogins { get; set; }

        [NotNull]
        public virtual DbSet<UploadPackageDbm>? UploadPackages { get; set; }

        [NotNull]
        public virtual DbSet<UploadPackageFileDbm>? UploadPackageFiles { get; set; }

        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
            CSUploaderModelConfiguration.Configure(modelBuilder);
            //CSUploaderDbInitializer initializer = new(modelBuilder);
            //Database.SetInitializer<CSUploaderDbContext>(initializer);
        }

        private void Configure()
        {
            Configuration.LazyLoadingEnabled = true;
            Configuration.ProxyCreationEnabled = true;
        }
    }
}
