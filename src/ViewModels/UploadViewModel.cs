// <copyright file="UploadViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;

namespace CSUploader.ViewModels;

public partial class UploadViewModel : ObservableObject
{
    private readonly PackageManager _packageManager;
    private readonly FileHosterLoginRepository _fileHosterLoginRepository;
    private readonly IDialogService _dialogService;
    private readonly IAppLogger _logger;
    private readonly AppSettings _settings;

    // Action to switch tab (set by MainViewModel)
    public Action<int>? SwitchToTab { get; set; }

    // ── Input fields ──

    [ObservableProperty]
    private string inputDirectory = string.Empty;

    public UploadViewModel(
        PackageManager packageManager,
        FileHosterLoginRepository fileHosterLoginRepository,
        IDialogService dialogService,
        IAppLogger logger,
        AppSettings settings)
    {
        _packageManager = packageManager;
        _fileHosterLoginRepository = fileHosterLoginRepository;
        _dialogService = dialogService;
        _logger = logger;
        _settings = settings;
    }

    // ── File hosters ──

    public ObservableCollection<FileHosterSelectionViewModel> FileHosters { get; } = [];

    public async Task LoadFileHostersAsync(CancellationToken cancellationToken = default)
    {
        FileHosters.Clear();

        foreach (string fileHosterName in FileHosterClient.FileHosters.Keys)
        {
            FileHosterLoginDto[] accounts = await _fileHosterLoginRepository.FindAsync(fileHosterName, cancellationToken);
            FileHosters.Add(new FileHosterSelectionViewModel(fileHosterName, accounts));
        }
    }

    // ── Commands ──

    [RelayCommand]
    private void BrowseInputDirectory()
    {
        string? folder = _dialogService.BrowseFolder(
            string.IsNullOrEmpty(InputDirectory) ? null : InputDirectory,
            "Select Input Directory");

        if (folder is not null)
        {
            InputDirectory = folder;
        }
    }

    [RelayCommand]
    private void Upload()
    {
        // Validate input directory
        if (string.IsNullOrWhiteSpace(InputDirectory) || !Directory.Exists(InputDirectory))
        {
            _dialogService.ShowError("Please select a valid input directory.");
            return;
        }

        List<string> directories = [InputDirectory];

        // Verify files exist
        if (!directories.Any(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any()))
        {
            _dialogService.ShowError("No files found in input directory.");
            return;
        }

        foreach (string directory in directories)
        {
            PackageOptions? options = CreatePackageOptions(directory);
            if (options is null)
            {
                return;
            }

            try
            {
                _packageManager.AddAndStartPackage(options);
            }
            catch (Exception ex)
            {
                _logger.Log(this, LogType.Error, $"Failed to add upload job: {ex}");
                _dialogService.ShowError($"Error: {ex.Message}");
            }
        }

        // Switch to Uploads tab
        SwitchToTab?.Invoke(1);
    }

    // ── Private helpers ──

    private PackageOptions? CreatePackageOptions(string directory)
    {
        PackageOptions options = new()
        {
            DirectoryPath = directory,
            Logger = _logger
        };

        // Gather selected file hosters
        foreach (FileHosterSelectionViewModel hoster in FileHosters)
        {
            if (!hoster.Use)
            {
                continue;
            }

            var client = FileHosterClient.FindByHost(hoster.FileHosterName, Protocol.Http, _logger);
            if (client is not null)
            {
                // Use selected account, or empty DTO for anonymous uploads
                FileHosterLoginDto account = hoster.SelectedAccount ?? new FileHosterLoginDto
                {
                    FileHosterName = hoster.FileHosterName,
                };
                options.FileHosters[client] = account;
            }
        }

        if (options.FileHosters.Count == 0)
        {
            _dialogService.ShowError("Please select at least one file hoster.");
            return null;
        }

        return options;
    }

}
