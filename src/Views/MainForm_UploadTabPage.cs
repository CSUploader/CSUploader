// <copyright file="MainForm_UploadTabPage.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.RegularExpressions;
using CSUploader.Controls.Models;
using CSUploader.Lib;
using CSUploader.Lib.Compression.ZevenZip;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace CSUploader.Views;

public partial class MainForm : Form
{
    private List<FileHosterListViewModel> Models { get; set; } = [];

    public void MainForm_UploadTabPage_Load(object? sender, EventArgs e)
    {
        cbUploadInputEnableCompression.CheckedChanged += CbUploadInputEnableCompression_CheckedChanged;
        tbUploadInputDirectory.TextChanged += TbUploadInputPackageNamingExpression_TextChanged;
        tbUploadInputDirectoryPattern.TextChanged += TbUploadInputPackageNamingExpression_TextChanged;
        tbUploadInputPackageNamingExpression.TextChanged += TbUploadInputPackageNamingExpression_TextChanged;

        // Set values
        GUIHelper.SetItems(cbUpload7zCompressionLevel, ZevenZip.CompressionLevels, SevenZip.CompressionLevel.None);
        GUIHelper.SetItems(cbUpload7zCompressionMethod, ZevenZip.CompressionMethods, SevenZip.CompressionMethod.Lzma2);
        GUIHelper.SetItems(cbUpload7zDictionarySize, ZevenZip.DictionarySizes, 0);
        GUIHelper.SetItems(cbUpload7zWordSize, ZevenZip.WordSizes, 0);
        GUIHelper.SetItems(cbUpload7zSolidBlockSize, ZevenZip.SolidBlockSizes, 0);
        GUIHelper.SetItems(cbUpload7zSplitVolumeBytes, ZevenZip.SplitVolumeBytes);
        lblUpload7zCPU.Text = $"/ {Environment.ProcessorCount}";

        for (int i = 1; i <= Environment.ProcessorCount; i++)
        {
            cbUpload7zNumberCPUThreads.Items.Add(i.ToString());
        }

        cbUpload7zNumberCPUThreads.SelectedIndex = 0;

        btnUploadInputBrowse.Click += BtnUploadInputBrowse_Click;
        btnUpload7zOutputBrowse.Click += BtnUpload7zOutputBrowse_Click;
        btnUploadUpload.Click += BtnUploadUpload_Click;

        fhlvUploadFileHosters.FileHostersImageList = ilFileHosters;
        fhlvUploadFileHosters.SetHeaderFormatStyle(tlvHeaderFormatStyle);
    }

    private async void MainForm_UploadTabPage_Focus(object? sender, EventArgs e)
    {
        await ProgressForm.ExecuteAsync(this, "Loading file hosters...", false, async (form, cancellationToken) =>
        {
            await LoadFileHostersAsync(cancellationToken);
        });
    }

    private void TbUploadInputPackageNamingExpression_TextChanged(object? sender, EventArgs e)
    {
        string input = Path.GetFileName(tbUploadInputDirectory.Text);
        string regularExpression = tbUploadInputDirectoryPattern.Text;
        if (string.IsNullOrEmpty(regularExpression) || string.IsNullOrEmpty(input))
        {
            tbIploadInputPackageNamingResult.Text = string.Empty;
            return;
        }

        try
        {
            Regex regex = new(regularExpression, RegexOptions.Singleline | RegexOptions.Compiled);
            Match match = regex.Match(input);

            string result = tbUploadInputPackageNamingExpression.Text;
            for (int i = 0; i < match.Groups.Count; i++)
            {
                Group g = match.Groups[i];
                result = result.Replace("{" + i + "}", g.Value, StringComparison.Ordinal);
            }

            tbIploadInputPackageNamingResult.Text = result;
        }
        catch
        {
            tbIploadInputPackageNamingResult.Text = "Invalid regular expression";
        }
    }

    private void BtnUploadInputBrowse_Click(object? sender, EventArgs e)
    {
        string initialDirectory = tbUploadInputDirectory.TextLength > 0 ? tbUploadInputDirectory.Text : string.Empty;
        CommonOpenFileDialog dialog = new()
        {
            InitialDirectory = initialDirectory,
            IsFolderPicker = true,
            AllowNonFileSystemItems = true,
            Title = "Select directory"
        };

        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            tbUploadInputDirectory.Text = dialog.FileName;
        }
    }

    private void CbUploadInputEnableCompression_CheckedChanged(object? sender, EventArgs e)
    {
        gbUploadCompression.Enabled = tbUploadInputPackageNamingExpression.Enabled = tbIploadInputPackageNamingResult.Enabled = cbUploadInputEnableCompression.Checked;
    }

    private void RbUploadCompressionRar_CheckedChanged(object? sender, EventArgs e)
    {
        gbUploadCompression7z.Enabled = false;
        gbUploadCompressionRar.Enabled = true;
    }

    private void RbUploadCompression7z_CheckedChanged(object? sender, EventArgs e)
    {
        gbUploadCompression7z.Enabled = true;
        gbUploadCompressionRar.Enabled = false;
    }

    private void BtnUpload7zOutputBrowse_Click(object? sender, EventArgs e)
    {
        string initialDirectory = tbUpload7zOutputDirectory.TextLength > 0 ? tbUpload7zOutputDirectory.Text : _settings.TempArchiveDirectory;
        CommonOpenFileDialog dialog = new()
        {
            InitialDirectory = initialDirectory,
            IsFolderPicker = true
        };

        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            tbUpload7zOutputDirectory.Text = dialog.FileName;
        }
    }

    private void BtnUploadUpload_Click(object? sender, EventArgs e)
    {
        // Check input directory
        if (!GUIHelper.ParseDirectoryPath(tbUploadInputDirectory, out string uploadInputDirectory, "Input directory not found"))
        {
            return;
        }

        List<string> directories = [];
        if (!string.IsNullOrEmpty(tbUploadInputDirectoryPattern.Text))
        {
            // Validate regex
            if (!GUIHelper.ParseRegularExpression(tbUploadInputDirectoryPattern, out Regex? regex, "Invalid regular expression"))
            {
                return;
            }

            FindDirectories(uploadInputDirectory, regex, directories);
        }
        else
        {
            directories.Add(uploadInputDirectory);
        }

        // Make sure there are files to upload
        if (!directories.Any(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any()))
        {
            GUIHelper.Error(tbUploadInputDirectory, "No files in input directory found");
            return;
        }

        foreach (string directory in directories)
        {
            PackageOptions? options = CreatePackageOptions(directory);
            if (options == null)
            {
                GUIHelper.Error(this, "Failed to create package options.");
                return;
            }

            try
            {
                packageManager.AddAndStartPackage(options);
            }
            catch (Exception ex)
            {
                _logger.Log(this, LogType.Error, $"Failed to add upload job: {ex}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Clear values
        //tbUploadInputDirectory.Clear();
        //tbUpload7zOutputDirectory.Clear();

        // Refresh tab before focusing it
        MainForm_UploadsTabPage_Refresh(this, e);

        // Focus uploads tab
        tcMain.SelectedTab = tpUploads;
    }

    private async Task LoadFileHostersAsync(CancellationToken cancellationToken = default)
    {
        Models = [];

        List<FileHosterClient?> fileHosters = FileHosterClient.FileHosters.Select(f => FileHosterClient.FindByHost(f.Key, Protocol.Http)).Where(f => f != null).ToList();
        foreach (FileHosterClient? fileHoster in fileHosters)
        {
            if (FileHosterLoginManager == null || fileHoster == null)
            {
                continue;
            }

            FileHosterListViewModel model = new()
            {
                Use = true,
                FileHoster = fileHoster,
                Accounts = await FileHosterLoginManager.FindAsync(fileHoster.Name, cancellationToken)
            };

            Models.Add(model);
        }

        fhlvUploadFileHosters.SetItems(Models);
    }

    private PackageOptions? CreatePackageOptions(string directory)
    {
        PackageOptions options = new()
        {
            DirectoryPath = directory
        };

        if (cbUploadInputEnableCompression.Checked)
        {
            if (rbUploadCompression7z.Checked)
            {
                if (!GUIHelper.ParseString(tbUpload7zOutputDirectory, out string uploadOutputDirectory, "Output directory is empty"))
                {
                    return null;
                }

                int splitVolumeBytes = 0;
                if (cbUpload7zSplitVolumeBytes.Text.Length > 0)
                {
                    KeyValuePair<long, string> kvp = ZevenZip.SplitVolumeBytes.FirstOrDefault(s => string.Equals(s.Value, cbUpload7zSplitVolumeBytes.Text, StringComparison.OrdinalIgnoreCase));
                    if (kvp.Value != null)
                    {
                        splitVolumeBytes = Convert.ToInt32(kvp.Key);
                    }
                    else if (GUIHelper.ParseSize(cbUpload7zSplitVolumeBytes, out long bytes, "Invalid bytes value"))
                    {
                        splitVolumeBytes = (int)bytes;
                    }
                    else
                    {
                        return null;
                    }
                }

                if ((tbUpload7zPassword.Text.Length > 0 || tbUpload7zPassword2.Text.Length > 0) && !string.Equals(tbUpload7zPassword.Text, tbUpload7zPassword2.Text, StringComparison.Ordinal))
                {
                    GUIHelper.Error(tbUpload7zPassword, "Passwords do not match");
                    return null;
                }

                options.CompressionOptions = new PackageCompressionOptions
                {
                    Compressor = new ZevenZipCompressor
                    {
                        Options = new ZevenZip.CompressionOptions
                        {
                            CompressionLevel = ZevenZip.CompressionLevels.ElementAt(cbUpload7zCompressionLevel.SelectedIndex).Key,
                            CompressionMethod = ZevenZip.CompressionMethods.ElementAt(cbUpload7zCompressionMethod.SelectedIndex).Key,
                            DictionarySize = ZevenZip.DictionarySizes.ElementAt(cbUpload7zDictionarySize.SelectedIndex).Key,
                            SolidBlockSize = ZevenZip.SolidBlockSizes.ElementAt(cbUpload7zSolidBlockSize.SelectedIndex).Key,
                            SplitVolumeBytes = splitVolumeBytes,
                            Password = tbUpload7zPassword.Text
                        }
                    },
                    OutputDirectoryPath = uploadOutputDirectory,
                    ArchivePassword = tbUpload7zPassword.Text
                };
            }
            else if (rbUploadCompressionRar.Checked)
            {
                // TODO: Add command line rar support
            }
        }

        foreach (FileHosterListViewModel model in fhlvUploadFileHosters.GetSelectedItems())
        {
            FileHosterClient? fileHoster = FileHosterClient.FileHosters.Where(fh => fh.Key == model.FileHoster?.Name).Select(fh => FileHosterClient.FindByHost(fh.Key, Protocol.Http)).FirstOrDefault();
            if (fileHoster != null && model.SelectedAccount != null)
            {
                options.FileHosters.Add(fileHoster, model.SelectedAccount);
            }
        }

        return options;
    }

    private static void FindDirectories(string directoryPath, Regex expression, List<string> directories)
    {
        if (expression.IsMatch(directoryPath))
        {
            directories.Add(directoryPath);
        }
        else
        {
            foreach (string dir in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                FindDirectories(dir, expression, directories);
            }
        }
    }
}
