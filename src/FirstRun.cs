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
    public static void InitializeDatabase(IServiceProvider services)
    {
        IDbContextFactory<CSUploaderDbContext> factory = services.GetRequiredService<IDbContextFactory<CSUploaderDbContext>>();
        using CSUploaderDbContext ctx = factory.CreateDbContext();
        ctx.Database.EnsureCreated();

        Logger.Current.Log(null, LogType.Status, "Initialized database");
    }
}
