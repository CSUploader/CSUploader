// <copyright file="FileInfoResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.Serialization;

namespace CSUploader.Upload.FileHosters.Models.Rapidu
{
    // POST /getFileDetails/
    [Serializable]
    public class FileInfoResponse : Dictionary<string, FileInfo>
    {
        public FileInfoResponse()
            : base()
        {
        }

        public FileInfoResponse(int capacity)
            : base(capacity)
        {
        }

        public FileInfoResponse(IEqualityComparer<string> comparer)
            : base(comparer)
        {
        }

        public FileInfoResponse(IDictionary<string, FileInfo> dictionary)
            : base(dictionary)
        {
        }

        public FileInfoResponse(int capacity, IEqualityComparer<string> comparer)
            : base(capacity, comparer)
        {
        }

        public FileInfoResponse(IDictionary<string, FileInfo> dictionary, IEqualityComparer<string> comparer)
            : base(dictionary, comparer)
        {
        }

        protected FileInfoResponse(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
