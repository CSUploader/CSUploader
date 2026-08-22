// <copyright file="UploadWizardViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;

namespace CSUploader.ViewModels;

/// <summary>
/// The Upload Wizard's shell: one child ViewModel per step — <see cref="Sources"/> (step 0),
/// <see cref="Hosters"/> (step 1), <see cref="Summary"/> (step 2) — plus what spans them: step
/// navigation, the start-mode options (step 3), and the final Finish.
/// <para>
/// The children are constructed HERE (not DI-registered) because the steps share one state
/// machine and the parent owns its seams: the summary-dirty bit (set by file AND hoster
/// selection changes, consumed on entry to step 2), the revalidate-hosters callback the sources
/// step invokes on selection changes, and <see cref="CanGoNext"/>, which reads the hoster step's
/// validation verdict and the summary step's capacity verdict. All cross-step signals are
/// synchronous callbacks/events fired from the same call sites the pre-split code ran them, so
/// the selection→validation→summary ordering is unchanged.
/// </para>
/// </summary>
public partial class UploadWizardViewModel : ObservableObject
{
    private readonly PackageManager packageManager;
    private readonly IDialogService dialogService;
    private readonly IAppLogger logger;
    private readonly AppSettings settings;

    public UploadWizardViewModel(
        PackageManager packageManager,
        FileHosterLoginRepository fileHosterLoginRepository,
        IDialogService dialogService,
        IAppLogger logger,
        AppSettings settings,
        IFileHosterRegistry? fileHosterRegistry = null,
        IAccountVerifier? accountVerifier = null,
        SettingRepository? settingRepository = null)
    {
        this.packageManager = packageManager;
        this.dialogService = dialogService;
        this.logger = logger;
        this.settings = settings;

        // Construction order matters only for the collection references (Hosters reads
        // Sources.Files; Summary reads both); the callbacks close over `this`, so Sources'
        // revalidate lambda safely reaches the Hosters instance created after it — nothing
        // invokes a callback during construction.
        Sources = new WizardSourcesViewModel(
            dialogService,
            logger,
            markSummaryDirty: () => _summaryDirty = true,
            revalidateHosters: () => Hosters!.RecomputeHosterValidation(),
            settings,
            settingRepository);
        Hosters = new WizardHostersViewModel(
            fileHosterLoginRepository,
            dialogService,
            logger,
            Sources.Files,
            markSummaryDirty: () => _summaryDirty = true,
            fileHosterRegistry,
            accountVerifier);

        // The wizard OPENS filtered to whichever upload mode the user configured; set before any
        // subscriber exists, so this seeds the state rather than firing a re-filter at nobody.
        // "Clear filter" in the wizard still returns to Both — see ClearHosterFilter.
        Hosters.AccountFilter = settings.WizardHosterAccountFilter;
        Summary = new WizardSummaryViewModel(
            logger,
            Sources.Files,
            Hosters.FileHosters,
            fileHosterRegistry,
            accountVerifier);

        // The window binds the shell's CanGoNext; the children own the state it reads. Their
        // events fire synchronously from the exact sites the pre-split code raised
        // OnPropertyChanged(nameof(CanGoNext)) on the one object.
        Hosters.ValidationStateChanged += (_, _) => OnPropertyChanged(nameof(CanGoNext));
        Summary.CapacityStateChanged += (_, _) => OnPropertyChanged(nameof(CanGoNext));
    }

    /// <summary>Step 0 — sources, the file list, the tree, filtering and selection.</summary>
    public WizardSourcesViewModel Sources { get; }

    /// <summary>Step 1 — the hoster list, its filters, limit validation and in-step account add.</summary>
    public WizardHostersViewModel Hosters { get; }

    /// <summary>Step 2 — per-hoster summaries, orphans, the capacity fit and storage refresh.</summary>
    public WizardSummaryViewModel Summary { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(IsLastStep))]
    [NotifyPropertyChangedFor(nameof(NextButtonText))]
    public partial int CurrentStep { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScheduledMode))]
    public partial UploadStartMode StartMode { get; set; } = UploadStartMode.Immediately;

    [ObservableProperty]
    public partial DateTime ScheduledDate { get; set; } = DateTime.Now.Date.AddDays(1);

    [ObservableProperty]
    public partial string ScheduledTime { get; set; } = "00:00";

    public bool IsScheduledMode => StartMode == UploadStartMode.Scheduled;

    public bool CanGoBack => CurrentStep > 0;

    /// <summary>
    /// Disables the Next button on the hoster-selection step (CurrentStep==1) when a
    /// hoster's declared limits are violated in a way the user must resolve manually —
    /// either too many files for the package, or every used hoster has zero files within
    /// its size limit. Size warnings on their own are informational (oversized files are
    /// dropped at upload time) and don't block. The Summary step (CurrentStep==2) blocks
    /// Next only while a hoster has more bytes checked than its account's available storage
    /// (<see cref="WizardSummaryViewModel.HasOverCapacity"/>) — orphan files still just surface a
    /// warning banner the user may proceed past.
    /// </summary>
    public bool CanGoNext => CurrentStep switch
    {
        1 => Hosters.HasSelectedHoster && !Hosters.HasHardBlock,
        2 => !Summary.HasOverCapacity,
        _ => true,
    };

    public bool IsLastStep => CurrentStep == 3;

    /// <summary>True when a Page 1/2 selection changed since the Summary was last built, so the next
    /// entry to step 2 rebuilds it. Starts true (first entry always builds), cleared after a build,
    /// and set again by any file/hoster selection change (the children's mark-summary-dirty callback)
    /// — so a Back from the later start-mode step (no selection change) preserves the user's manual
    /// Page 3 checkbox edits instead of re-fitting.</summary>
    private bool _summaryDirty = true;

    partial void OnCurrentStepChanged(int value)
    {
        // Lazy-(re)build the summary on entry to step 2, but ONLY when a Page 1/2 selection actually
        // changed since it was last built (_summaryDirty). Otherwise keep the existing summaries so a
        // Back from the later "when to start" step (step 3) preserves the user's manual checkbox edits
        // and the auto-fit result rather than wiping them with a fresh fit.
        if (value == 2 && _summaryDirty)
        {
            Summary.RecomputeSummary();
            _summaryDirty = false;
        }
    }

    public string NextButtonText => IsLastStep
        ? Localizer.Instance["Wizard_Btn_Add"]
        : Localizer.Instance["Wizard_Btn_Next"];

    [ObservableProperty]
    public partial bool Completed { get; set; }

    // Prefill the scheduled date + time to NOW the moment the user picks Scheduled, so they adjust from the
    // current time rather than the tomorrow-at-midnight placeholder. Fires only on a real transition INTO
    // Scheduled (re-selecting the already-active mode raises no change), so it won't clobber edits unless the
    // user leaves Scheduled and returns. HH:mm to match the field's format hint.
    partial void OnStartModeChanged(UploadStartMode value)
    {
        if (value == UploadStartMode.Scheduled)
        {
            DateTime now = DateTime.Now;
            ScheduledDate = now.Date;
            ScheduledTime = now.ToString("HH:mm", CultureInfo.CurrentCulture);
        }
    }

    [RelayCommand]
    private async Task GoNextAsync()
    {
        if (CurrentStep == 0)
        {
            // One list, however it was filled — the folder walk and the file picker both append to it,
            // so there is a single thing to validate rather than a per-mode branch.
            if (Sources.Files.Count == 0)
            {
                await dialogService.ShowErrorAsync(Localizer.Instance["Wizard_Validation_PickAtLeastOneFile"]);
                return;
            }

            if (string.IsNullOrWhiteSpace(Sources.PackageTitle))
            {
                await dialogService.ShowErrorAsync(Localizer.Instance["Wizard_Validation_TitleRequired"]);
                return;
            }

            if (!Sources.Files.Any(f => f.IsSelected))
            {
                await dialogService.ShowErrorAsync(Localizer.Instance["Wizard_Validation_PickFile"]);
                return;
            }

            await Hosters.LoadFileHostersAsync();
            CurrentStep = 1;
        }
        else if (CurrentStep == 1)
        {
            // Advance to the new Summary step. OnCurrentStepChanged populates Summaries.
            CurrentStep = 2;
        }
        else if (CurrentStep == 2)
        {
            // Summary → Start/Schedule.
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

    private async Task<bool> StartUploadAsync()
    {
        PackageOptions options = new()
        {
            Title = Sources.PackageTitle.Trim(),
            Logger = logger,
            Settings = settings,
            SelectedFiles = [.. Sources.Files.Where(f => f.IsSelected).Select(f => f.FullPath)],
        };

        foreach (FileHosterSelectionViewModel hoster in Hosters.FileHosters)
        {
            if (!hoster.Use)
            {
                continue;
            }

            var client = FileHosterClient.FindByHost(hoster.FileHosterName, Protocol.Http, logger);
            if (client is not null)
            {
                FileHosterLoginDto account = hoster.SelectedAccount ?? new FileHosterLoginDto
                {
                    FileHosterName = hoster.FileHosterName,
                    IsAnonymous = hoster.SupportsAnonymous,
                };
                options.FileHosters[client] = account;
            }
        }

        // Per-hoster file selection from the Summary page's capacity fit (null when there are no
        // summaries → the package keeps its default cross-product; size/quota filters still apply).
        options.IncludedFilesPerHoster = Summary.BuildIncludedFilesPerHoster();

        if (options.FileHosters.Count == 0)
        {
            await dialogService.ShowErrorAsync(Localizer.Instance["Wizard_Validation_PickHoster"]);
            return false;
        }

        Hosters.SaveStickySelections();

        try
        {
            switch (StartMode)
            {
                case UploadStartMode.Immediately:
                    await packageManager.AddAndStartPackageAsync(options);
                    packageManager.StartPackages();
                    break;

                case UploadStartMode.Later:
                    await packageManager.AddPackageOnlyAsync(options);
                    break;

                case UploadStartMode.Scheduled:
                    if (!TimeSpan.TryParse(ScheduledTime, out TimeSpan time))
                    {
                        time = TimeSpan.Zero;
                    }

                    Package package = await packageManager.AddPackageOnlyAsync(options);
                    DateTime scheduled = ScheduledDate.Date + time;
                    packageManager.ScheduleDelayedStart(package, scheduled);
                    break;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.Log(this, LogType.Error, $"Failed to add upload job: {ex}");
            await dialogService.ShowErrorAsync(string.Format(CultureInfo.CurrentCulture, Localizer.Instance["Wizard_Error_Format"], ex.Message));
            return false;
        }
    }
}
