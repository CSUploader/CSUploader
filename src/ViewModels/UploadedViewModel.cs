// <copyright file="UploadedViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Services;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

public partial class UploadedViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly UploadPackageRepository _uploadPackageRepository;
    private readonly UploadPackageFileRepository _uploadPackageFileRepository;
    private readonly IDialogService _dialogService;
    private readonly IAppLogger _logger;

    /// <summary>
    /// Exposed to the view's code-behind so the column-toggle menu can persist visibility
    /// via <see cref="Lib.UI.DataGridColumnVisibilityPersistence"/>. Optional in tests.
    /// </summary>
    internal SettingRepository? SettingRepo { get; }

    /// <summary>
    /// Exposed to the view's code-behind so the "Reset columns" entry can prompt via
    /// the standard opt-out confirmation flow.
    /// </summary>
    internal IDialogService DialogServiceForView => _dialogService;

    public UploadedViewModel(
        UploadPackageRepository uploadPackageRepository,
        UploadPackageFileRepository uploadPackageFileRepository,
        PackageManager packageManager,
        IDialogService dialogService,
        IAppLogger logger,
        SettingRepository? settingRepo = null)
    {
        _uploadPackageRepository = uploadPackageRepository;
        _uploadPackageFileRepository = uploadPackageFileRepository;
        _dialogService = dialogService;
        _logger = logger;
        SettingRepo = settingRepo;
        packageManager.PackageCompleted += OnPackageCompleted;
        packageManager.FileCompleted += OnFileCompleted;
    }

    /// <summary>
    /// Flat list of files across all completed packages. Grouped by <see cref="UploadedFileRow.PackageName"/> in the view.
    /// </summary>
    public ObservableCollection<UploadedFileRow> Files { get; } = [];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        (UploadPackageFileDto File, string PackageName)[] rows =
            await _uploadPackageFileRepository.GetDoneFilesWithPackageNameAsync(cancellationToken);

        Files.Clear();
        foreach ((UploadPackageFileDto file, string packageName) in rows)
        {
            Files.Add(new UploadedFileRow
            {
                FileId = file.Id,
                PackageId = file.PackageId,
                PackageName = packageName,
                FileName = file.FileName ?? string.Empty,
                FileDirectory = file.FileDirectory ?? string.Empty,
                FileSize = file.FileSize,
                FileHosterName = file.FileHosterName ?? file.FileHoster ?? string.Empty,
                FinishedDateTime = file.FinishedDateTime,
                FileUrl = file.FileUrl,
                FileHash = file.FileHash,
            });
        }
    }

    [RelayCommand]
    private void CopyUrls(IList? selectedItems)
    {
        if (selectedItems is null)
        {
            return;
        }

        string[] urls = [.. selectedItems
            .OfType<UploadedFileRow>()
            .Select(r => r.FileUrl)
            .Where(u => !string.IsNullOrEmpty(u))
            .Cast<string>()];

        try
        {
            if (urls.Length == 0)
            {
                Clipboard.Clear();
                _logger.Log(this, LogType.Status, Localizer.Instance["Logs_Status_NoUrlsClipboardCleared"]);
                return;
            }

            Clipboard.SetText(string.Join(Environment.NewLine, urls));
            _logger.Log(this, LogType.Status, string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Logs_Status_CopiedUrls_Format"], urls.Length));
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Copy URL failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync(IList? selectedItems)
    {
        if (selectedItems is null)
        {
            return;
        }

        UploadedFileRow[] rows = [.. selectedItems.OfType<UploadedFileRow>()];
        if (rows.Length == 0)
        {
            return;
        }

        string msg = rows.Length == 1
            ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Uploaded_Remove_Single_Format"], rows[0].FileName)
            : string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Uploaded_Remove_Many_Format"], rows.Length);
        if (!_dialogService.ShowOptOutConfirmation(ConfirmationKeys.RemoveUploadedEntry, msg, Localizer.Instance["Uploaded_Remove_Title"]))
        {
            return;
        }

        try
        {
            int[] fileIds = [.. rows.Select(r => r.FileId).Where(id => id > 0).Distinct()];
            if (fileIds.Length > 0)
            {
                // Soft-delete: keep the rows in the DB (so uploaded-history data is preserved)
                // and just filter them out of the Uploaded tab's query.
                await _uploadPackageFileRepository.HideAsync(fileIds);
            }

            _logger.Log(this, LogType.Status, string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Logs_Status_HiddenFiles_Format"], fileIds.Length));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Remove failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            FileName = $"csuploader-uploaded-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            UploadPackageDto[] packages = await _uploadPackageRepository.GetCompletedAsync();
            string json = JsonSerializer.Serialize(packages, JsonOptions);
            await File.WriteAllTextAsync(dialog.FileName, json);
            _logger.Log(this, LogType.Status, string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Logs_Status_ExportedPackages_Format"], packages.Length, dialog.FileName));
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Export failed: {ex.Message}");
        }
    }

    private void OnPackageCompleted(object? sender, Package package) => RefreshOnUiThread();

    private void OnFileCompleted(object? sender, PackageFile file) => RefreshOnUiThread();

    private void RefreshOnUiThread()
    {
        Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await LoadAsync();
            }
            catch (Exception ex)
            {
                _logger.Log(this, LogType.Error, $"Uploaded tab refresh failed: {ex.Message}");
            }
        });
    }
}
