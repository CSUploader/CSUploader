// <copyright file="DbManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public partial class DbManager
    {
        public DbManager(FileHosterLoginManager fileHosterLoginManager, SettingManager settingManager, UploadPackageManager uploadPackageManager, UploadPackageFileManager uploadPackageFileManager)
        {
            FileHosterLoginManager = fileHosterLoginManager;

            SettingManager = settingManager;

            UploadPackageManager = uploadPackageManager;

            UploadPackageFileManager = uploadPackageFileManager;
        }

        protected FileHosterLoginManager FileHosterLoginManager { get; private set; }

        protected SettingManager SettingManager { get; private set; }

        protected UploadPackageManager UploadPackageManager { get; private set; }

        protected UploadPackageFileManager UploadPackageFileManager { get; private set; }
    }
}
