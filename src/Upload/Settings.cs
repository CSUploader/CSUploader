// <copyright file="Settings.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Extensions;
using System.Text.RegularExpressions;

namespace CSUploader.Upload
{
    public static class Settings
    {
        private static string? tempArchiveDirectory;

        private static int? uploadsTabPageRefreshTimer;

        private static int? maxConcurrentCPUJobs;

        private static int? maxConcurrentUploadJobs;

        private static int? speedLimit;

        /// <summary>
        /// Gets the default temporary archive directory.
        /// </summary>
        public static string DefaultTempArchiveDirectory { get; } = PathExtensions.GetTemporaryDirectory();

        /// <summary>
        /// Gets the default refresh timer for the uploads listview, in seconds.
        /// </summary>
        public static int DefaultUploadsTabPageRefreshTimer { get; } = 1;

        /// <summary>
        /// Gets the default maximum concurrent CPU intensive jobs.
        /// </summary>
        public static int DefaultMaxConcurrentCPUJobs { get; } = 1;

        /// <summary>
        /// Gets the default maximum concurrent upload jobs.
        /// </summary>
        public static int DefaultMaxConcurrentUploadJobs { get; } = 5;

        /// <summary>
        /// Gets the URL regex.
        /// </summary>
        public static Regex UrlRegex { get; } = new Regex("(?:https?[:]\\/\\/)?(?:www\\.)?[-a-zA-Z0-9@:%._\\+~#=]{2,256}\\.[a-z]{2,6}\\b(?:[-a-zA-Z0-9@:%_\\+.~#?&//=]*)", RegexOptions.Compiled);

        /// <summary>
        /// Gets or sets the temporary archive directory.
        /// </summary>
        /// <value>
        /// The temporary archive directory.
        /// </value>
        public static string TempArchiveDirectory
        {
            get
            {
                return tempArchiveDirectory ?? DefaultTempArchiveDirectory;
            }

            set
            {
                tempArchiveDirectory = value;
            }
        }

        /// <summary>
        /// Gets or sets the uploads tab page refresh timer.
        /// </summary>
        /// <value>
        /// The uploads tab page refresh timer.
        /// </value>
        public static int UploadsTabPageRefreshTimer
        {
            get
            {
                return uploadsTabPageRefreshTimer ?? DefaultUploadsTabPageRefreshTimer;
            }

            set
            {
                uploadsTabPageRefreshTimer = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum concurrent CPU intensive jobs.
        /// </summary>
        /// <value>
        /// The maximum concurrent CPU intensive jobs.
        /// </value>
        public static int MaxConcurrentCPUJobs
        {
            get
            {
                return maxConcurrentCPUJobs ?? DefaultMaxConcurrentCPUJobs;
            }

            set
            {
                maxConcurrentCPUJobs = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum concurrent upload jobs.
        /// </summary>
        /// <value>
        /// The maximum concurrent upload jobs.
        /// </value>
        public static int MaxConcurrentUploadJobs
        {
            get
            {
                return maxConcurrentUploadJobs ?? DefaultMaxConcurrentUploadJobs;
            }

            set
            {
                maxConcurrentUploadJobs = value;
            }
        }

        /// <summary>
        /// Gets or sets the speed limit.
        /// </summary>
        /// <value>
        /// The speed limit.
        /// </value>
        public static int? SpeedLimit
        {
            get
            {
                return speedLimit;
            }

            set
            {
                speedLimit = value;
            }
        }
    }
}
