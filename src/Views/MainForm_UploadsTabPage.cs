// <copyright file="MainForm_UploadsTabPage.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using BrightIdeasSoftware;
using CSUploader.Components;
using CSUploader.Lib;
using CSUploader.Upload;

namespace CSUploader.Views
{
    /// <summary>
    /// Main form.
    /// </summary>
    /// <seealso cref="Form" />
    public partial class MainForm : Form
    {
        private readonly PackageManager packageManager = new();

        private readonly Dictionary<PackageJob, string> packageJobStrings = new()
        {
            { PackageJob.Compression, "Compression" },
            { PackageJob.Hashing, "Hashing" },
            { PackageJob.Upload, "Uploading" }
        };

        private readonly Dictionary<JobStatus, string> jobStatusStrings = new()
        {
            { JobStatus.Cancelled, "cancelled" },
            { JobStatus.Failed, "failed" },
            { JobStatus.Paused, "paused" },
            { JobStatus.Queued, "queued" },
            { JobStatus.Running, "running" },
            { JobStatus.Success, "success" }
        };

        // Array of tuples for each status that require an image different from the default status image
        private readonly Tuple<PackageJob, JobStatus, string>[] statusImages = new Tuple<PackageJob, JobStatus, string>[]
        {
            new Tuple<PackageJob, JobStatus, string>(PackageJob.Compression, JobStatus.Running, "status_compressing"),
            new Tuple<PackageJob, JobStatus, string>(PackageJob.Hashing, JobStatus.Running, "status_hashing"),
            new Tuple<PackageJob, JobStatus, string>(PackageJob.Upload, JobStatus.Running, "status_uploading")
        };

        public void MainForm_UploadsTabPage_Load(object? sender, EventArgs e)
        {
            // Button handlers
            btnUploadsStart.Click += BtnUploadsStart_Click;
            btnUploadsPause.Click += BtnUploadsPause_Click;
            btnUploadsStop.Click += BtnUploadsStop_Click;
            new ToolTip().SetToolTip(btnUploadsStart, "Start Downloads");
            new ToolTip().SetToolTip(btnUploadsPause, "Pause Mode. Limits global speed to 10KiB/s");
            new ToolTip().SetToolTip(btnUploadsStop, "Stops all running Downloads");

            // Package added handler
            packageManager.PackageAdded += PackageManager_PackageAdded;

            // TreeListView handler
            tlvUploads.CellRightClick += TlvUploads_CellRightClick;
            tlvUploads.KeyUp += TlvUploads_KeyUp;

            // ToolStripMenuItem handlers
            tsmiUploadsRetry.Click += TsmiUploadsRetry_Click;
            tsmiUploadsStop.Click += TsmiUploadsStop_Click;

            // Timers
            tmrUploadsTabPageRefresh.Interval = Settings.UploadsTabPageRefreshTimer * 1000;
            tmrUploadsTabPageRefresh.Tick += TimerUploadsTabPageRefresh_Tick;
            tmrUploadsTabPageRefresh.Start();

            // TreeListView items
            tlvUploads.SetObjects(packageManager.Packages);
            tlvUploads.Roots = packageManager.Packages;
            tlvUploads.CanExpandGetter = (object rowObject) => (rowObject is Package package) && package.Any();
            tlvUploads.ChildrenGetter = (object rowObject) => rowObject as Package;

            // ObjectListView renderers
            olvUploadsName.Renderer = new ImageRenderer();
            olvUploadsName.AspectName = nameof(PackageDetails.Name);
            olvUploadsName.ImageGetter = (object rowObject) =>
            {
                if (rowObject is Package package)
                {
                    if (package.Status?.Job == PackageJob.Compression && package?.Status.Status == JobStatus.Running)
                    {
                        return ilPackage.Images.Keys.Cast<string>().Where(k => k.StartsWith("package_compressing.")).Select(k => ilPackage.Images[k]).FirstOrDefault();
                    }

                    return ilPackage.Images.Keys.Cast<string>().Where(k => k.StartsWith($"package_{(tlvUploads.IsExpanded(rowObject) ? "open" : "closed")}.")).Select(k => ilPackage.Images[k]).FirstOrDefault();
                }
                else if (rowObject is PackageFile packageFile)
                {
                    return ilFileTypes.Images.Keys.Cast<string>().Where(s => s.StartsWith($"filetype_{packageFile.FileType}.", StringComparison.OrdinalIgnoreCase)).Select(k => ilFileTypes.Images[k]).FirstOrDefault();
                }

                return null;
            };
            olvUploadsSize.AspectGetter = GetBytesString((PackageDetails m) => m.Size);
            olvUploadsHoster.Renderer = new ImageRenderer();
            olvUploadsHoster.AspectGetter = (object rowObject) =>
            {
                if (rowObject is Package package)
                {
                    List<Image> images = new();
                    foreach (FileHosterClient fileHoster in package.FileHosters)
                    {
                        images.AddRange(ilFileHosters.Images.Keys.Cast<string>().Where(k => k.StartsWith($"filehoster_{fileHoster.Name}.", StringComparison.OrdinalIgnoreCase)).Select(k => ilFileHosters.Images[k]));
                    }

                    return images;
                }
                else if (rowObject is PackageFile packageFile)
                {
                    return ilFileHosters.Images.Keys.Cast<string>().Where(k => k.StartsWith($"filehoster_{packageFile.FileHosters.First().Name}.", StringComparison.OrdinalIgnoreCase)).Select(k => ilFileHosters.Images[k]).FirstOrDefault();
                }

                return null;
            };

            //olvUploadsConnection.Renderer = new FlagRenderer
            //olvUploadsGateway.AspectGetter = (object model) => ((UploadGroupManager)model).FileHosters.Select(fh => fh.IconName).ToArray();
            olvUploadsStatus.Renderer = new MultiImageTextRenderer();
            olvUploadsStatus.ImageGetter = (object rowObject) =>
            {
                if (rowObject is Package package)
                {
                    if (!package.Any())
                    {
                        string key = statusImages.Where(i => i.Item1 == PackageJob.Compression && i.Item2 == package.Status?.Status).Select(i => i.Item3).FirstOrDefault() ?? $"status_{package.Status?.Status}";
                        return ilStatus.Images.Keys.Cast<string>().Where(s => s.StartsWith($"{key}.", StringComparison.OrdinalIgnoreCase)).Select(k => ilStatus.Images[k]).FirstOrDefault();
                    }

                    Dictionary<string, Image> images = new();
                    foreach (PackageFile packageFile in package)
                    {
                        string key = statusImages.Where(i => i.Item1 == packageFile.Status?.Job && i.Item2 == packageFile.Status?.Status).Select(i => i.Item3).FirstOrDefault() ?? $"status_{packageFile.Status?.Status}";
                        if (!images.ContainsKey(key))
                        {
                            Image? image = ilStatus.Images.Keys.Cast<string>().Where(s => s.StartsWith($"{key}.", StringComparison.OrdinalIgnoreCase)).Select(k => ilStatus.Images[k]).FirstOrDefault();
                            if (image != null)
                            {
                                images.Add(key, image);
                            }
                        }
                    }

                    return images.Select(kvp => kvp.Value).ToList();
                }
                else if (rowObject is PackageFile packageFile)
                {
                    string key = statusImages.Where(i => i.Item1 == packageFile.Status?.Job && i.Item2 == packageFile.Status?.Status).Select(i => i.Item3).FirstOrDefault() ?? $"status_{packageFile.Status?.Status}";
                    return ilStatus.Images.Keys.Cast<string>().Where(s => s.StartsWith($"{key}.", StringComparison.OrdinalIgnoreCase)).Select(k => ilStatus.Images[k]).FirstOrDefault();
                }

                return null;
            };
            olvUploadsStatus.AspectGetter = (object rowObject) =>
            {
                if (rowObject is Package package && package.Any())
                {
                    if (package.All(p => p.Status?.Status == JobStatus.Success))
                    {
                        return "Finished";
                    }

                    return string.Empty;
                }

                if (rowObject is not PackageDetails packageDetails || packageDetails.Status == null)
                {
                    return string.Empty;
                }

                if (!packageJobStrings.TryGetValue(packageDetails.Status.Job, out string? packageJob) || !jobStatusStrings.TryGetValue(packageDetails.Status.Status, out string? jobStatus))
                {
                    return string.Empty;
                }

                return $"{packageJob} {jobStatus}";
            };
            olvUploadsAddedDate.AspectGetter = GetDateTimeString((PackageDetails m) => m.AddedDate);
            olvUploadsUploadMode.AspectGetter = (object rowObject) => (rowObject is PackageDetails pd) && pd.UploadMode.HasValue ? pd.UploadMode.Value.ToString() : string.Empty;
            olvUploadsFinishedDate.AspectGetter = GetDateTimeString((PackageDetails m) => m.FinishedDate);
            olvUploadsDuration.AspectGetter = GetTimeSpanString((PackageDetails m) => m.Duration);
            olvUploadsSpeed.AspectGetter = GetBytesString((PackageDetails m) => m.Speed);
            olvUploadsETA.AspectGetter = GetTimeSpanString((PackageDetails m) => m.TimeRemaining);
            olvUploadsBytesLoaded.AspectGetter = GetBytesString((PackageDetails m) => m.BytesLoaded);
            olvUploadsBytesRemaining.AspectGetter = GetBytesString((PackageDetails m) => m.BytesRemaining);
            Color barColor = ColorTranslator.FromHtml("#b3d9ed");
            Color frameSurroundingColor = ColorTranslator.FromHtml("#8ea2ab");
            olvUploadsProgress.Renderer = new BarTextRenderer(new Pen(frameSurroundingColor, 1), Brushes.Black) { MinimumValue = 0.0, MaximumValue = 100.0, GradientStartColor = barColor, GradientEndColor = barColor };
            olvUploadsProgress.AspectGetter = (object rowObject) => (rowObject is PackageDetails pd) && pd.Progress.HasValue ? pd.Progress.Value : null as double?;
            //olvUploadsPriority
            //olvUploadsAvailability
            olvUploadsSaveFrom.AspectName = nameof(PackageDetails.SaveFrom);
            olvUploadsCompressionPassword.AspectName = nameof(PackageDetails.Password);
            olvUploadsFileCount.AspectGetter = GetInt32String((PackageDetails m) => (m is Package p) && p.FileCount.HasValue ? p.FileCount : null);
            olvUploadsError.AspectGetter = (object rowObject) => (rowObject is PackageDetails m) ? m.Error : null;
            olvUploadsFileUrl.AspectGetter = (object rowObject) => (rowObject is PackageFile packageFile) ? packageFile.FileUrl : null;
            olvUploadsEnabledDisabled.AspectName = nameof(PackageDetails.Enabled);
        }

        public void MainForm_UploadsTabPage_Focus(object? sender, EventArgs e)
        {
        }

        public void MainForm_UploadsTabPage_Refresh(object? sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke((MethodInvoker)delegate { MainForm_UploadsTabPage_Refresh(sender, e); });
                return;
            }

            // Set items to force a refresh
            tlvUploads.SetObjects(packageManager.Packages);
        }

        private static AspectGetterDelegate GetInt32String(Func<PackageDetails, int?> action)
        {
            return (object rowObject) =>
            {
                if (rowObject is not PackageDetails m)
                {
                    return null;
                }

                int? value = action(m);
                return value.HasValue ? value.Value.ToString() : string.Empty;
            };
        }

        private static AspectGetterDelegate GetBytesString(Func<PackageDetails, long?> action)
        {
            return (object rowObject) =>
            {
                if (rowObject is not PackageDetails m)
                {
                    return null;
                }

                long? bytes = action(m);
                return bytes.HasValue ? ByteUnit.FromBytes(bytes.Value, ByteBase.Binary).ToFriendlyString() : string.Empty;
            };
        }

        private static AspectGetterDelegate GetTimeSpanString(Func<PackageDetails, TimeSpan?> action)
        {
            return (object rowObject) =>
            {
                if (rowObject is not PackageDetails m)
                {
                    return null;
                }

                TimeSpan? timeSpan = action(m);
                if (!timeSpan.HasValue)
                {
                    return string.Empty;
                }

                string format = timeSpan.Value.Hours > 0
                        ? @"h\h\:mm\m\:ss\s"
                        : (timeSpan.Value.Minutes > 0)
                            ? @"mm\m\:ss\s"
                            : @"ss\s";
                return timeSpan.Value.ToString(format);
            };
        }

        private static AspectGetterDelegate GetDateTimeString(Func<PackageDetails, DateTime?> action)
        {
            return (object rowObject) =>
            {
                if (rowObject is not PackageDetails m)
                {
                    return null;
                }

                DateTime? dateTime = action(m);
                if (!dateTime.HasValue)
                {
                    return string.Empty;
                }

                return dateTime.Value.ToString("yyyy/MM/dd HH:mm:ss");
            };
        }

        private void BtnUploadsStop_Click(object? sender, EventArgs e)
        {
            packageManager.StopPackages();
        }

        private void BtnUploadsPause_Click(object? sender, EventArgs e)
        {
            packageManager.PausePackages(!packageManager.IsPaused);
        }

        private void BtnUploadsStart_Click(object? sender, EventArgs e)
        {
            packageManager.PausePackages(true);
        }

        private void TimerUploadsTabPageRefresh_Tick(object? sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke((MethodInvoker)delegate { TimerUploadsTabPageRefresh_Tick(sender, e); });
                return;
            }

            MainForm_UploadsTabPage_Refresh(sender, e);
        }

        private void PackageManager_PackageAdded(object? sender, PackageAddedEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke((MethodInvoker)delegate { PackageManager_PackageAdded(sender, e); });
                return;
            }

            if (e.ParentPackage != null && !tlvUploads.IsExpanded(e.ParentPackage))
            {
                // Refresh to make sure package gets added
                MainForm_UploadsTabPage_Refresh(this, e);

                // Expand the newly added package
                tlvUploads.Expand(e.ParentPackage);
            }
        }

        private void TlvUploads_CellRightClick(object? sender, CellRightClickEventArgs e)
        {
            if (e.Model == null)
            {
                // Not a right click on a row
                return;
            }

            // Make all items visible
            foreach (ToolStripItem item in cmsUploadsPackageFile.Items.Cast<ToolStripItem>())
            {
                item.Visible = true;
            }

            // Hide items
            //if (e.Model is Package package)
            //{
            //    if (package.Status.Compression.HasValue && package.Status.Compression.Value != JobStatus.Failed && !package.Any(pf => pf.Status. == PackageStatus.Error))
            //    {
            //        ToolStripItem retry = cmsUploadsPackageFile.Items.Cast<ToolStripItem>().Where(ts => ts.Name == "Retry").FirstOrDefault();
            //        if (retry != null)
            //        {
            //            retry.Visible = false;
            //        }
            //    }
            //}
            //else if (e.Model is PackageFile packageFile)
            //{
            //    PackageStatus status = PackageManager.GetState(packageFile);
            //    switch (status)
            //    {
            //        case PackageStatus.Error:
            //            break;

            //        case PackageStatus.Compressing:
            //            break;
            //    }
            //}

            e.MenuStrip = cmsUploadsPackageFile;
        }

        private void TlvUploads_KeyUp(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                PackageDetails[] packagesDetails = tlvUploads.SelectedObjects.Cast<PackageDetails>().ToArray();

                // Ask for confirmation if a package or package file is still running
                if (packagesDetails.Any(pf => pf.Status?.Status == JobStatus.Running)
                    && !GUIHelper.ConfirmDialog("Hey", "One or more package file(s) are still running, are you sure you want to remove the selected package(s)?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning))
                {
                    return;
                }

                foreach (PackageDetails packageDetails in packagesDetails)
                {
                    if (packageDetails is Package package)
                    {
                        packageManager.RemovePackage(package);
                    }
                    else if (packageDetails is PackageFile packageFile)
                    {
                        if (packagesDetails.Any(pd => ReferenceEquals(pd, packageFile.Package)))
                        {
                            // Don't remove package file if the package is selected for removal (package will handle removal)
                            continue;
                        }

                        packageManager.RemovePackageFile(packageFile);
                    }
                }

                tlvUploads.RemoveObjects(tlvUploads.SelectedObjects);
            }
        }

        private void TsmiUploadsRetry_Click(object? sender, EventArgs e)
        {
            PackageDetails[] packagesDetails = tlvUploads.SelectedObjects.Cast<PackageDetails>().ToArray();

            foreach (PackageDetails packageDetails in packagesDetails)
            {
                packageManager.StartPackage(packageDetails);
            }
        }

        private void TsmiUploadsStop_Click(object? sender, EventArgs e)
        {
            PackageDetails[] packagesDetails = tlvUploads.SelectedObjects.Cast<PackageDetails>().ToArray();

            // Ask for confirmation if a package or package file is still uploading
            if (packagesDetails.Any(pf => pf.Status?.Status == JobStatus.Running) &&
                !GUIHelper.ConfirmDialog("Hey", "One or more package file(s) are still running, are you sure you want to stop the selected package(s)?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning))
            {
                return;
            }

            bool failed = false;
            foreach (PackageDetails packageDetails in packagesDetails)
            {
                if (packageDetails.Status?.Job == PackageJob.Compression && packageDetails.Status?.Status == JobStatus.Running)
                {
                    if (!failed)
                    {
                        failed = true;
                    }

                    continue;
                }

                PackageManager.StopPackage(packageDetails);
            }

            if (failed)
            {
                GUIHelper.Error(null, "SevenZip does not support cancelling compressing");
            }
        }
    }
}
