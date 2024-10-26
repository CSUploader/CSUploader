// <copyright file="CSUploaderDbInitializer.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using SQLite.CodeFirst;
using System.Data.Entity;

namespace CSUploader.Dal
{
    public class CSUploaderDbInitializer : SqliteDropCreateDatabaseWhenModelChanges<CSUploaderDbContext>, IDatabaseInitializer<CSUploaderDbContext>
    {
        public CSUploaderDbInitializer(DbModelBuilder modelBuilder)
            : base(modelBuilder, typeof(CSUploaderHistory))
        {
        }

        protected override void Seed(CSUploaderDbContext context)
        {
            // Here you can seed your core data if you have any.
        }
    }
}
