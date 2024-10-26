// <copyright file="CSUploaderHistory.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using SQLite.CodeFirst;

namespace CSUploader.Dal
{
    public class CSUploaderHistory : IHistory
    {
        public int Id { get; set; }

        public string? Hash { get; set; }

        public string? Context { get; set; }

        public DateTime CreateDate { get; set; }
    }
}
