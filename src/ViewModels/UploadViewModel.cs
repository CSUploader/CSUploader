// <copyright file="UploadViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Compression.ZevenZip;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;
using SevenZip;

namespace CSUploader.ViewModels;

public partial class UploadViewModel : ObservableObject
{
    private readonly PackageManager _packageManager;
    private readonly FileHosterLoginManager _fileHosterLoginManager;
    private readonly IDialogService _dialogService;
    private readonly IAppLogger _logger;

    // Action to switch tab (set by MainViewModel)
    public Action<int>? SwitchToTab { get; set; }

    // ── Input fields ──

    [ObservableProperty]
    private string inputDirectory = string.Empty;

    [ObservableProperty]
    private string directoryPattern = string.Empty;

    [ObservableProperty]
    private string packageNamingExpression = string.Empty;

    [ObservableProperty]
    private string packageNamingResult = string.Empty;

    // ── Compression fields ──

    [ObservableProperty]
    private bool enableCompression;

    [ObservableProperty]
    private bool is7zSelected = true;

    [ObservableProperty]
    private bool isRarSelected;

    [ObservableProperty]
    private string outputDirectory = string.Empty;

    [ObservableProperty]
    private string archivePassword = string.Empty;

    [ObservableProperty]
    private string archivePasswordConfirm = string.Empty;

    [ObservableProperty]
    private KeyValuePair<CompressionLevel, string> selectedCompressionLevel;

    [ObservableProperty]
    private KeyValuePair<CompressionMethod, string> selectedCompressionMethod;

    [ObservableProperty]
    private KeyValuePair<int, string> selectedDictionarySize;

    [ObservableProperty]
    private KeyValuePair<int, string> selectedWordSize;

    [ObservableProperty]
    private KeyValuePair<long, string> selectedSolidBlockSize;

    [ObservableProperty]
    private KeyValuePair<long, string> selectedSplitVolume;

    [ObservableProperty]
    private int cpuThreads = 1;

    public UploadViewModel(
        PackageManager packageManager,
        FileHosterLoginManager fileHosterLoginManager,
        IDialogService dialogService,
        IAppLogger logger)
    {
        _packageManager = packageManager;
        _fileHosterLoginManager = fileHosterLoginManager;
        _dialogService = dialogService;
        _logger = logger;

        // Set defaults
        SelectedCompressionLevel = ZevenZip.CompressionLevels.FirstOrDefault(x => x.Key == CompressionLevel.None);
        SelectedCompressionMethod = ZevenZip.CompressionMethods.FirstOrDefault(x => x.Key == CompressionMethod.Lzma2);
        SelectedDictionarySize = ZevenZip.DictionarySizes.First();
        SelectedWordSize = ZevenZip.WordSizes.First();
        SelectedSolidBlockSize = ZevenZip.SolidBlockSizes.First();
        SelectedSplitVolume = ZevenZip.SplitVolumeBytes.First();
    }

    // ── Compression option sources for ComboBoxes ──
    // These must be instance properties for WPF data binding (cannot be static).

#pragma warning disable CA1822
    public Dictionary<CompressionLevel, string> CompressionLevels => ZevenZip.CompressionLevels;
    public Dictionary<CompressionMethod, string> CompressionMethods => ZevenZip.CompressionMethods;
    public Dictionary<int, string> DictionarySizes => ZevenZip.DictionarySizes;
    public Dictionary<int, string> WordSizes => ZevenZip.WordSizes;
    public Dictionary<long, string> SolidBlockSizes => ZevenZip.SolidBlockSizes;
    public Dictionary<long, string> SplitVolumes => ZevenZip.SplitVolumeBytes;
    public int MaxCpuThreads => Environment.ProcessorCount;
#pragma warning restore CA1822

    // ── File hosters ──

    public ObservableCollection<FileHosterSelectionViewModel> FileHosters { get; } = [];

    public async Task LoadFileHostersAsync(CancellationToken cancellationToken = default)
    {
        FileHosters.Clear();

        foreach (string fileHosterName in FileHosterClient.FileHosters.Keys)
        {
            FileHosterLoginDto[] accounts = await _fileHosterLoginManager.FindAsync(fileHosterName, cancellationToken);
            FileHosters.Add(new FileHosterSelectionViewModel(fileHosterName, accounts));
        }
    }

    // ── Regex preview (triggered when input properties change) ──

    partial void OnInputDirectoryChanged(string value) => UpdateNamingPreview();
    partial void OnDirectoryPatternChanged(string value) => UpdateNamingPreview();
    partial void OnPackageNamingExpressionChanged(string value) => UpdateNamingPreview();

    private void UpdateNamingPreview()
    {
        string input = Path.GetFileName(InputDirectory);
        if (string.IsNullOrEmpty(DirectoryPattern) || string.IsNullOrEmpty(input))
        {
            PackageNamingResult = string.Empty;
            return;
        }

        try
        {
            Regex regex = new(DirectoryPattern, RegexOptions.Singleline | RegexOptions.Compiled);
            Match match = regex.Match(input);

            string result = PackageNamingExpression;
            for (int i = 0; i < match.Groups.Count; i++)
            {
                Group g = match.Groups[i];
                result = result.Replace("{" + i + "}", g.Value, StringComparison.Ordinal);
            }

            PackageNamingResult = result;
        }
        catch
        {
            PackageNamingResult = "Invalid regular expression";
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
    private void BrowseOutputDirectory()
    {
        string? folder = _dialogService.BrowseFolder(
            string.IsNullOrEmpty(OutputDirectory) ? AppSettings.Current.TempArchiveDirectory : OutputDirectory,
            "Select Output Directory");

        if (folder is not null)
        {
            OutputDirectory = folder;
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

        // Find directories matching pattern
        List<string> directories = [];
        if (!string.IsNullOrEmpty(DirectoryPattern))
        {
            try
            {
                Regex regex = new(DirectoryPattern, RegexOptions.Singleline | RegexOptions.Compiled);
                FindDirectories(InputDirectory, regex, directories);
            }
            catch
            {
                _dialogService.ShowError("Invalid regular expression.");
                return;
            }
        }
        else
        {
            directories.Add(InputDirectory);
        }

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
            DirectoryPath = directory
        };

        if (EnableCompression && Is7zSelected)
        {
            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                _dialogService.ShowError("Output directory is empty.");
                return null;
            }

            // Validate passwords match
            if ((!string.IsNullOrEmpty(ArchivePassword) || !string.IsNullOrEmpty(ArchivePasswordConfirm))
                && !string.Equals(ArchivePassword, ArchivePasswordConfirm, StringComparison.Ordinal))
            {
                _dialogService.ShowError("Passwords do not match.");
                return null;
            }

            options.CompressionOptions = new PackageCompressionOptions
            {
                Compressor = new ZevenZipCompressor
                {
                    Options = new ZevenZip.CompressionOptions
                    {
                        CompressionLevel = SelectedCompressionLevel.Key,
                        CompressionMethod = SelectedCompressionMethod.Key,
                        DictionarySize = SelectedDictionarySize.Key,
                        WordSize = SelectedWordSize.Key,
                        SolidBlockSize = SelectedSolidBlockSize.Key,
                        SplitVolumeBytes = (int)SelectedSplitVolume.Key,
                        Password = ArchivePassword,
                        NumberCPUThreads = CpuThreads,
                    }
                },
                OutputDirectoryPath = OutputDirectory,
                TemporaryDirectory = AppSettings.Current.TempArchiveDirectory,
                ArchivePassword = ArchivePassword,
            };
        }

        // Gather selected file hosters
        foreach (FileHosterSelectionViewModel hoster in FileHosters)
        {
            if (!hoster.Use || hoster.SelectedAccount is null)
            {
                continue;
            }

            FileHosterClient? client = FileHosterClient.FindByHost(hoster.FileHosterName, Protocol.Http);
            if (client is not null)
            {
                options.FileHosters[client] = hoster.SelectedAccount;
            }
        }

        if (options.FileHosters.Count == 0)
        {
            _dialogService.ShowError("Please select at least one file hoster with an account.");
            return null;
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
