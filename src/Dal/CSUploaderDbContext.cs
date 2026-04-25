// <copyright file="CSUploaderDbContext.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Microsoft.EntityFrameworkCore;

namespace CSUploader.Dal;

public class CSUploaderDbContext : DbContext
{
    private static readonly string DefaultDbPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "CSUploader.db");

    public CSUploaderDbContext()
    {
    }

    public CSUploaderDbContext(DbContextOptions<CSUploaderDbContext> options)
        : base(options)
    {
    }

    public DbSet<SettingDbm> Settings => Set<SettingDbm>();

    public DbSet<FileHosterLoginDbm> FileHosterLogins => Set<FileHosterLoginDbm>();

    public DbSet<UploadPackageDbm> UploadPackages => Set<UploadPackageDbm>();

    public DbSet<UploadPackageFileDbm> UploadPackageFiles => Set<UploadPackageFileDbm>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite($"Data Source={DefaultDbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileHosterLoginDbm>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.AccountType)
                .HasConversion<int>();
        });

        modelBuilder.Entity<SettingDbm>(entity => entity.HasKey(e => e.Id));

        modelBuilder.Entity<UploadPackageDbm>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasMany(e => e.Files)
                .WithOne(f => f.Package)
                .HasForeignKey(f => f.PackageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UploadPackageFileDbm>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
        });
    }
}
