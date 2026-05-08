// <copyright file="UploadWizardViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

public partial class UploadWizardViewModel(
    PackageManager packageManager,
    FileHosterLoginRepository fileHosterLoginRepository,
    IDialogService dialogService,
    IAppLogger logger) : ObservableObject
{
    private static readonly List<FileHosterSelectionViewModel> _stickyHosters = [];

    private readonly PackageManager _packageManager = packageManager;
    private readonly FileHosterLoginRepository _fileHosterLoginRepository = fileHosterLoginRepository;
    private readonly IDialogService _dialogService = dialogService;
    private readonly IAppLogger _logger = logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(NextButtonText))]
    private int currentStep;

    [ObservableProperty]
    private string directoryPath = string.Empty;

    [ObservableProperty]
    private string packageTitle = string.Empty;

    [ObservableProperty]
    private string fileFilter = string.Empty;

    public ObservableCollection<FileEntry> Files { get; } = [];

    public ObservableCollection<FileHosterSelectionViewModel> FileHosters { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScheduledMode))]
    private UploadStartMode startMode = UploadStartMode.Immediately;

    [ObservableProperty]
    private DateTime scheduledDate = DateTime.Now.Date.AddDays(1);

    [ObservableProperty]
    private string scheduledTime = "00:00";

    public bool IsScheduledMode => StartMode == UploadStartMode.Scheduled;

    public bool CanGoBack => CurrentStep > 0;

    public bool CanGoNext => CurrentStep <= 3;

    public bool IsLastStep => CurrentStep == 3;

    public string NextButtonText => IsLastStep
        ? Localizer.Instance["Wizard_Btn_Add"]
        : Localizer.Instance["Wizard_Btn_Next"];

    [ObservableProperty]
    private bool completed;

    partial void OnFileFilterChanged(string value)
    {
        ApplyFilter();
    }

    [RelayCommand]
    private void BrowseDirectory()
    {
        string? folder = _dialogService.BrowseFolder(
            string.IsNullOrEmpty(DirectoryPath) ? null : DirectoryPath,
            Localizer.Instance["Wizard_Step0_BrowseDialogTitle"]);

        if (folder is not null)
        {
            DirectoryPath = folder;
        }
    }

    [RelayCommand]
    private async Task GoNextAsync()
    {
        if (CurrentStep == 0)
        {
            if (string.IsNullOrWhiteSpace(DirectoryPath) || !Directory.Exists(DirectoryPath))
            {
                _dialogService.ShowError(Localizer.Instance["Wizard_Validation_PickValidDir"]);
                return;
            }

            LoadFiles();
            CurrentStep = 1;
        }
        else if (CurrentStep == 1)
        {
            if (!Files.Any(f => f.IsSelected))
            {
                _dialogService.ShowError(Localizer.Instance["Wizard_Validation_PickFile"]);
                return;
            }

            await LoadFileHostersAsync();
            CurrentStep = 2;
        }
        else if (CurrentStep == 2)
        {
            CurrentStep = 3;
        }
        else if (CurrentStep == 3)
        {
            if (await StartUploadAsync())
            {
                Completed = true;
            }
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        if (CurrentStep > 0)
        {
            CurrentStep--;
        }
    }

    [RelayCommand]
    private async Task AddAccountForHosterAsync(FileHosterSelectionViewModel? hoster)
    {
        if (hoster is null)
        {
            return;
        }

        string[] availableHosters = [hoster.FileHosterName];
        FileHosterLoginDto? result = _dialogService.ShowAddAccountDialog(
            hoster.FileHosterName,
            availableHosters,
            Localizer.Instance["EditAccount_AddTitle"]);

        if (result is null)
        {
            return;
        }

        try
        {
            await _fileHosterLoginRepository.InsertAsync(result);
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Failed to save new {hoster.FileHosterName} account: {ex}");
            _dialogService.ShowError(string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["Wizard_Error_Format"], ex.Message));
            return;
        }

        FileHosterLoginDto[] accounts = await _fileHosterLoginRepository.FindAsync(hoster.FileHosterName);
        hoster.SetAccounts(accounts);

        // Auto-tick "Use" now that an account exists — saves the user a click and
        // matches the flow they were already in (they clicked "Add account…" because
        // they wanted to upload to this hoster).
        hoster.Use = true;
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (FileEntry file in Files)
        {
            file.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (FileEntry file in Files)
        {
            file.IsSelected = false;
        }
    }

    private void LoadFiles()
    {
        Files.Clear();
        FileFilter = string.Empty;
        if (string.IsNullOrEmpty(PackageTitle))
        {
            PackageTitle = Path.GetFileName(DirectoryPath) ?? DirectoryPath;
        }

        foreach (string filePath in Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(DirectoryPath, filePath);
            FileInfo fi = new(filePath);
            Files.Add(new FileEntry
            {
                FullPath = filePath,
                RelativePath = relativePath,
                FileName = fi.Name,
                Size = fi.Length,
                IsSelected = true,
                IsVisible = true,
            });
        }
    }

    private void ApplyFilter()
    {
        string filter = FileFilter.Trim();
        foreach (FileEntry file in Files)
        {
            if (string.IsNullOrEmpty(filter))
            {
                file.IsVisible = true;
            }
            else
            {
                file.IsVisible = file.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private async Task LoadFileHostersAsync()
    {
        if (FileHosters.Count > 0)
        {
            return;
        }

        foreach (string fileHosterName in FileHosterClient.FileHosters.Keys)
        {
            FileHosterLoginDto[] accounts = await _fileHosterLoginRepository.FindAsync(fileHosterName);

            FileHosterSelectionViewModel? sticky = _stickyHosters.Find(
                h => string.Equals(h.FileHosterName, fileHosterName, StringComparison.Ordinal));

            FileHosterSelectionViewModel vm = new(fileHosterName, accounts);
            if (sticky is not null)
            {
                vm.Use = sticky.Use;
                if (sticky.SelectedAccount is not null && accounts.Any(a => a.Id == sticky.SelectedAccount.Id))
                {
                    vm.SelectedAccount = accounts.First(a => a.Id == sticky.SelectedAccount.Id);
                }
            }

            FileHosters.Add(vm);
        }
    }

    private async Task<bool> StartUploadAsync()
    {
        PackageOptions options = new()
        {
            DirectoryPath = DirectoryPath,
            Title = string.IsNullOrWhiteSpace(PackageTitle) ? null : PackageTitle.Trim(),
            Logger = _logger,
            SelectedFiles = [.. Files.Where(f => f.IsSelected).Select(f => f.FullPath)],
        };

        foreach (FileHosterSelectionViewModel hoster in FileHosters)
        {
            if (!hoster.Use)
            {
                continue;
            }

            var client = FileHosterClient.FindByHost(hoster.FileHosterName, Protocol.Http, _logger);
            if (client is not null)
            {
                FileHosterLoginDto account = hoster.SelectedAccount ?? new FileHosterLoginDto
                {
                    FileHosterName = hoster.FileHosterName,
                };
                options.FileHosters[client] = account;
            }
        }

        if (options.FileHosters.Count == 0)
        {
            _dialogService.ShowError(Localizer.Instance["Wizard_Validation_PickHoster"]);
            return false;
        }

        SaveStickySelections();

        try
        {
            switch (StartMode)
            {
                case UploadStartMode.Immediately:
                    await _packageManager.AddAndStartPackageAsync(options);
                    _packageManager.StartPackages();
                    break;

                case UploadStartMode.Later:
                    await _packageManager.AddPackageOnlyAsync(options);
                    break;

                case UploadStartMode.Scheduled:
                    if (!TimeSpan.TryParse(ScheduledTime, out TimeSpan time))
                    {
                        time = TimeSpan.Zero;
                    }

                    Package package = await _packageManager.AddPackageOnlyAsync(options);
                    DateTime scheduled = ScheduledDate.Date + time;
                    _packageManager.ScheduleDelayedStart(package, scheduled);
                    break;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Log(this, LogType.Error, $"Failed to add upload job: {ex}");
            _dialogService.ShowError(string.Format(System.Globalization.CultureInfo.CurrentCulture, Localizer.Instance["Wizard_Error_Format"], ex.Message));
            return false;
        }
    }

    private void SaveStickySelections()
    {
        _stickyHosters.Clear();
        foreach (FileHosterSelectionViewModel hoster in FileHosters)
        {
            _stickyHosters.Add(hoster);
        }
    }
}

public partial class FileEntry : ObservableObject
{
    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isVisible = true;

    public string FullPath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long Size { get; set; }
}
