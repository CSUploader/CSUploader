// <copyright file="DbManagerFileHosterLogin.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Dal
{
    public partial class DbManager
    {
        public virtual Task<FileHosterLoginDto[]> GetFileHosterLoginsAsync(CancellationToken cancellationToken = default)
        {
            return FileHosterLoginManager.GetAllAsync(cancellationToken);
        }

        public virtual Task<FileHosterLoginDto[]> FindFileHosterLoginsAsync(string fileHosterName, CancellationToken cancellationToken = default)
        {
            return FileHosterLoginManager.FindAsync(fileHosterName, cancellationToken);
        }

        public virtual Task<FileHosterLoginDto?> FindFileHosterLoginsAsync(string fileHosterName, string username, CancellationToken cancellationToken = default)
        {
            return FileHosterLoginManager.FindAsync(fileHosterName, username);
        }
    }
}
