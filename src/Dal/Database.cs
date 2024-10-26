// <copyright file="Database.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public partial class Database : DbManager
    {
        public Database(FileHosterLoginManager fileHosterLoginManager, SettingManager settingManager, UploadPackageManager uploadPackageManager, UploadPackageFileManager uploadPackageFileManager)
            : base(fileHosterLoginManager, settingManager, uploadPackageManager, uploadPackageFileManager)
        {
        }
    }
}
