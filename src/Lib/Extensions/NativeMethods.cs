// <copyright file="NativeMethods.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Runtime.InteropServices;

namespace CSUploader.Extensions
{
    internal static class NativeMethods
    {
        private const uint SWP_NOSIZE = 0x0001;

        private const uint SWP_NOMOVE = 0x0002;

        private const uint TOPMOST_FLAGS = SWP_NOMOVE | SWP_NOSIZE;

        private static readonly IntPtr HWND_TOPMOST = new(-1);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        public static bool AlwaysOnTop(this Form form, bool alwaysOnTop)
        {
            return SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, TOPMOST_FLAGS);
        }
    }
}
