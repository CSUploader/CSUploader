// <copyright file="Logging.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Lib
{
    public static class Logging
    {
        private static readonly string FileName = $"{DateTime.Now:yyyyMMdd} debug.log";

        static Logging()
        {
            LogFile = File.Exists(FileName)
                ? File.AppendText(FileName)
                : File.CreateText(FileName);
        }

        private static TextWriter LogFile { get; set; }

        public static DialogResult ShowMessageBox(Exception ex, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            string fileName = Path.Combine(Directory.GetCurrentDirectory(), Logging.FileName);
            string msg = $"{text}" + Environment.NewLine + $"Saved error to file" + Environment.NewLine + $"{fileName}";

            DialogResult result = MessageBox.Show(msg, caption, buttons, icon);
            Write($"{text}: {result}" + Environment.NewLine + $"{ex}");
            return result;
        }

        public static void Write(string msg)
        {
            LogFile.WriteLine($"[{DateTime.Now:yyyy/MM/dd HH:mm:ss}] {msg}");
            LogFile.Flush();
        }
    }
}
