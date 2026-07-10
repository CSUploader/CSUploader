// <copyright file="UploadedViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
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
    private readonly IAppLogger _logger;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IClipboardService _clipboardService;

    /// <summary>
    /// Exposed to the view's code-behind so the column-toggle menu can persist visibility
    /// via the head-side <c>DataGridColumnVisibilityPersistence</c> helper. Optional in tests.
    /// </summary>
    internal SettingRepository? SettingRepo { get; }

    /// <summary>
    /// Exposed to the view's code-behind so the "Reset columns" entry can prompt via
    /// the standard opt-out confirmation flow.
    /// </summary>
    internal IDialogService DialogServiceForView { get; }

    public UploadedViewModel(
        UploadPackageRepository uploadPackageRepository,
        UploadPackageFileRepository uploadPackageFileRepository,
        PackageManager packageManager,
        IDialogService dialogService,
        IAppLogger logger,
        IUiDispatcher uiDispatcher,
        IClipboardService clipboardService,
        SettingRepository? settingRepo = null)
    {
        _uploadPackageRepository = uploadPackageRepository;
        _uploadPackageFileRepository = uploadPackageFileRepository;
        DialogServiceForView = dialogService;
        _logger = logger;
        _uiDispatcher = uiDispatcher;
        _clipboardService = clipboardService;
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
                // Invariant: FileHosterLoginId == 0 iff the upload was anonymous — the synthetic
                // anonymous DTO is never persisted (Id stays 0) and every real account row has
                // Id > 0. So 0 => the localized "(anonymous)"; otherwise the denormalized account
                // name (null/blank for rows persisted before the column existed). Don't persist a
                // real account with Id == 0 or it would mislabel here as anonymous.
                AccountDisplay = file.FileHosterLoginId == 0
                    ? Localizer.Instance["Wizard_Step2_AccountAnonymous"]
                    : (file.FileHosterAccount ?? string.Empty),
                FinishedDateTime = file.FinishedDateTime,
                StartedDateTime = file.StartDateTime,
                FileUrl = file.FileUrl,
                FileHash = file.FileHash,
            });
        }
    }

    [RelayCommand]
    private async Task CopyUrlsAsync(IList? selectedItems)
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
                await _clipboardService.ClearAsync();
                _logger.Log(this, LogType.Status, Localizer.Instance["Logs_Status_NoUrlsClipboardCleared"]);
                return;
            }

            await _clipboardService.SetTextAsync(string.Join(Environment.NewLine, urls));
            _logger.Log(this, LogType.Status, string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Logs_Status_CopiedUrls_Format"], urls.Length));
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Copy URL failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Currently focused row, driven from the DataGrid's SelectedItem so the per-column
    /// "Copy" submenu can locate the value to copy with just the column key as parameter.
    /// </summary>
    [ObservableProperty]
    private UploadedFileRow? selectedRow;

    /// <summary>
    /// The full multi-row selection, snapshotted by the view when the context menu opens.
    /// The per-column "Copy" commands operate on this so copying a column with several rows
    /// selected yields a value per row — <see cref="SelectedRow"/> alone is only the primary
    /// row (which is why "Copy → URL" used to copy just the first selected URL).
    /// </summary>
    public IReadOnlyList<UploadedFileRow> SelectedRows { get; set; } = [];

    /// <summary>
    /// Opens the <c>FileUrl</c> of every selected row in the default browser (distinct URLs only).
    /// Enabled when at least one selected row has a URL (older entries from before URL persistence
    /// have none).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenUrl))]
    private static void OpenUrl(IList? selectedItems)
    {
        foreach (string url in SelectedDistinctUrls(selectedItems))
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // Best-effort — see the same swallow in UploadsViewModel.OpenUrl.
            }
        }
    }

    /// <summary>The distinct, non-empty <c>FileUrl</c>s of the selected rows, in selection order.
    /// Pure + internal so the open-all-URLs behavior is unit-testable without launching a browser.</summary>
    internal static IReadOnlyList<string> SelectedDistinctUrls(IList? selectedItems)
        => selectedItems is null
            ? []
            : [.. selectedItems
                .OfType<UploadedFileRow>()
                .Select(r => r.FileUrl)
                .Where(u => !string.IsNullOrEmpty(u))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)];

    internal static bool CanOpenUrl(IList? selectedItems)
        => selectedItems is not null
            && selectedItems.OfType<UploadedFileRow>().Any(r => !string.IsNullOrEmpty(r.FileUrl));

    /// <summary>
    /// Copies the value of <paramref name="columnKey"/> for every selected row to the
    /// clipboard (one per line). Column keys mirror the resx <c>Uploaded_Col_*</c> suffix.
    /// </summary>
    [RelayCommand]
    private async Task CopyColumnAsync(string? columnKey)
    {
        if (BuildColumnCopyText(columnKey) is not { } text)
        {
            return;
        }

        try
        {
            await _clipboardService.SetTextAsync(text);
        }
        catch
        {
            // Swallow contention errors from the clipboard — copying must never crash the UI thread.
        }
    }

    /// <summary>
    /// Builds the clipboard payload for a per-column copy: that column's value for every row in
    /// <see cref="SelectedRows"/> (blank values skipped), newline-joined. Falls back to the
    /// primary <see cref="SelectedRow"/> when no multi-selection was captured. Returns null when
    /// there is nothing to copy. Separated from <see cref="CopyColumnCommand"/> so the value
    /// logic is unit-testable without touching the clipboard.
    /// </summary>
    internal string? BuildColumnCopyText(string? columnKey)
    {
        if (string.IsNullOrEmpty(columnKey))
        {
            return null;
        }

        IReadOnlyList<UploadedFileRow> rows = SelectedRows.Count > 0
            ? SelectedRows
            : (SelectedRow is { } only ? [only] : []);

        string[] values = [.. rows
            .Select(r => ColumnValueExtractor.Extract(r, columnKey, isUploadsTab: false))
            .Where(v => !string.IsNullOrEmpty(v))
            .Cast<string>()];

        return values.Length == 0 ? null : string.Join(Environment.NewLine, values);
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
        if (!await DialogServiceForView.ShowOptOutConfirmationAsync(ConfirmationKeys.RemoveUploadedEntry, msg, Localizer.Instance["Uploaded_Remove_Title"]))
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
        _uiDispatcher.Post(async () =>
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
