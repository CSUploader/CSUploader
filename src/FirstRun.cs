// <copyright file="FirstRun.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Dal;
using CSUploader.Lib;

namespace CSUploader
{
    public static class FirstRun
    {
        public static Database InitializeDatabase()
        {
            FileHosterLoginManager fileHosterLoginManager = new(new FileHosterLoginStore());

            SettingManager settingManager = new(new SettingStore());

            UploadPackageManager uploadPackageManager = new(new UploadPackageStore());

            UploadPackageFileManager uploadPackageFileManager = new(new UploadPackageFileStore());

            Database database = new(fileHosterLoginManager, settingManager, uploadPackageManager, uploadPackageFileManager);

            Logger.Log(null, LogType.Status, $"Initialized database");

            return database;
        }
    }
}
