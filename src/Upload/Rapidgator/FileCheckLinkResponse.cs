// <copyright file="FileCheckLinkResponse.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;

namespace CSUploader.Upload.Rapidgator
{
    public class FileCheckLinkResponse : IList<FileCheckLink>
    {
        private readonly List<FileCheckLink> checkLinks = new();

        public int Count => throw new NotImplementedException();

        public bool IsReadOnly => throw new NotImplementedException();

        public FileCheckLink this[int index] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public void Add(FileCheckLink item)
        {
            checkLinks.Add(item);
        }

        public void Clear()
        {
            checkLinks.Clear();
        }

        public bool Contains(FileCheckLink item)
        {
            return checkLinks.Contains(item);
        }

        public void CopyTo(FileCheckLink[] array, int arrayIndex)
        {
            checkLinks.CopyTo(array, arrayIndex);
        }

        public IEnumerator<FileCheckLink> GetEnumerator()
        {
            return checkLinks.GetEnumerator();
        }

        public int IndexOf(FileCheckLink item)
        {
            return checkLinks.IndexOf(item);
        }

        public void Insert(int index, FileCheckLink item)
        {
            checkLinks.Insert(index, item);
        }

        public bool Remove(FileCheckLink item)
        {
            return checkLinks.Remove(item);
        }

        public void RemoveAt(int index)
        {
            checkLinks.RemoveAt(index);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return checkLinks.GetEnumerator();
        }
    }
}
