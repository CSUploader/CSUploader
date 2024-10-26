// <copyright file="LogListViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;

namespace CSUploader.Controls.Models
{
    public class LogListViewModel
    {
        public LogType LogType { get; set; }

        public DateTime DateTime { get; set; }

        public string? Filename { get; set; }

        public string? Function { get; set; }

        public int LineNumber { get; set; }

        public string? Message { get; set; }

        public int ThreadId { get; set; }
    }
}
