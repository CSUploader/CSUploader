// <copyright file="MainForm_UploadedTabPage.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using BrightIdeasSoftware;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Upload;

namespace CSUploader.Views
{
    public partial class MainForm : Form
    {
        private List<UploadPackageDto> UploadPackages { get; set; } = new List<UploadPackageDto>();

        public void MainForm_UploadedTabPage_Load(object sender, EventArgs e)
        {
            // TreeListView handler
            tlvUploaded.CellRightClick += TlvUploads_CellRightClick;
            tlvUploaded.KeyUp += TlvUploads_KeyUp;

            // TreeListView items
            tlvUploaded.SetObjects(UploadPackages);
            tlvUploaded.Roots = UploadPackages;
            tlvUploaded.CanExpandGetter = (object rowObject) => (rowObject is Package package) && package.Any();
            tlvUploaded.ChildrenGetter = (object rowObject) => rowObject as Package;

            // ObjectListView renderers
            olvUploadedName.AspectGetter = (object rowObject) =>
            {
                if (rowObject is UploadPackageDto uploadPackage)
                {
                    return uploadPackage.Name;
                }
                else if (rowObject is UploadPackageFileDto uploadPackageFile)
                {
                    return uploadPackageFile.FileName;
                }

                return null;
            };
            olvUploadedSize.AspectGetter = (object rowObject) =>
            {
                if (rowObject is UploadPackageDto uploadPackage)
                {
                    return ByteUnit.FromBytes(uploadPackage.Sum(f => f.FileSize), ByteBase.Binary).ToFriendlyString();
                }
                else if (rowObject is UploadPackageFileDto uploadPackageFile)
                {
                    return uploadPackageFile.FileName;
                }

                return null;
            };
            olvUploadedHoster.Renderer = new ImageRenderer();
            olvUploadedHoster.AspectGetter = (object rowObject) =>
            {
                if (rowObject is UploadPackageDto uploadPackage)
                {
                    List<Image> images = new();
                    foreach (UploadPackageFileDto uploadPackageFile in uploadPackage.Files)
                    {
                        images.AddRange(ilFileHosters.Images.Keys.Cast<string>().Where(k => k.StartsWith($"filehoster_{uploadPackageFile.FileHosterName}.", StringComparison.OrdinalIgnoreCase)).Select(k => ilFileHosters.Images[k]));
                    }

                    return images;
                }
                else if (rowObject is UploadPackageFileDto uploadPackageFile)
                {
                    return ilFileHosters.Images.Keys.Cast<string>().Where(k => k.StartsWith($"filehoster_{uploadPackageFile.FileHoster}.", StringComparison.OrdinalIgnoreCase)).Select(k => ilFileHosters.Images[k]).FirstOrDefault();
                }

                return null;
            };
            olvUploadedAddedDate.AspectGetter = (object rowObject) =>
            {
                if (rowObject is UploadPackageFileDto uploadPackageFile)
                {
                    return uploadPackageFile.StartDateTime.ToString("yyyy/MM/dd HH:mm:ss");
                }

                return null;
            };
            olvUploadedFinishedDate.AspectGetter = (object rowObject) =>
            {
                if (rowObject is UploadPackageFileDto uploadPackageFile)
                {
                    return uploadPackageFile.FinishedDateTime.ToString("yyyy/MM/dd HH:mm:ss");
                }

                return null;
            };
            olvUploadedDuration.AspectGetter = (object rowObject) =>
            {
                if (rowObject is UploadPackageDto uploadPackage)
                {
                    DateTime firstDateTime = uploadPackage.OrderBy(f => f.StartDateTime).Select(f => f.StartDateTime).First();
                    DateTime lastDateTime = uploadPackage.OrderBy(f => f.FinishedDateTime).Select(f => f.FinishedDateTime).First();
                    TimeSpan timeSpan = lastDateTime - firstDateTime;
                    string format = timeSpan.Hours > 0
                                            ? @"h\h\:mm\m\:ss\s"
                                            : (timeSpan.Minutes > 0)
                                                ? @"mm\m\:ss\s"
                                                : @"ss\s";
                    return timeSpan.ToString(format);
                }
                else if (rowObject is UploadPackageFileDto uploadPackageFile)
                {
                    TimeSpan timeSpan = uploadPackageFile.FinishedDateTime - uploadPackageFile.StartDateTime;
                    string format = timeSpan.Hours > 0
                                            ? @"h\h\:mm\m\:ss\s"
                                            : (timeSpan.Minutes > 0)
                                                ? @"mm\m\:ss\s"
                                                : @"ss\s";
                    return timeSpan.ToString(format);
                }

                return null;
            };
            olvUploadedCompressionPassword.AspectGetter = (object rowObject) =>
            {
                if (rowObject is UploadPackageDto uploadPackage)
                {
                    DateTime firstDateTime = uploadPackage.OrderBy(f => f.StartDateTime).Select(f => f.StartDateTime).First();
                    DateTime lastDateTime = uploadPackage.OrderBy(f => f.FinishedDateTime).Select(f => f.FinishedDateTime).First();
                    TimeSpan timeSpan = lastDateTime - firstDateTime;
                    string format = timeSpan.Hours > 0
                                            ? @"h\h\:mm\m\:ss\s"
                                            : (timeSpan.Minutes > 0)
                                                ? @"mm\m\:ss\s"
                                                : @"ss\s";
                    return timeSpan.ToString(format);
                }

                return null;
            };
            olvUploadedFileCount.AspectGetter = (object rowObject) => (rowObject is UploadPackageDto uploadPackage) ? uploadPackage.Count() as int? : null;
            olvUploadedFileUrl.AspectGetter = (object rowObject) => (rowObject is UploadPackageFileDto uploadPackageFile) ? uploadPackageFile.FileUrl : null;
        }

        public void MainForm_UploadedTabPage_Focus(object? sender, EventArgs e)
        {
        }

        private async Task LoadUploadedAsync(CancellationToken cancellationToken = default)
        {
            UploadPackageDto[] packages = await Database.GetUploadPackagesAsync(cancellationToken);
            UploadPackages = packages.ToList();
        }
    }
}
