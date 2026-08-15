// <copyright file="HosterUploadSummary.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Localization;

namespace CSUploader.ViewModels;

/// <summary>
/// One row on the Upload Wizard's summary step: one hoster, the account chosen for it, and the
/// files eligible for it after the hoster's per-file size cap and per-package count cap. Each file
/// carries an independent <see cref="SummaryFileItem.Included"/> checkbox. For an account that
/// reports a storage quota, <see cref="AvailableBytes"/> is the remaining free space and the row
/// flips <see cref="IsOverCapacity"/> when the included files exceed it (which blocks the wizard's
/// Next). Hosters that end up with zero eligible files don't get a summary at all.
/// </summary>
public sealed partial class HosterUploadSummary : ObservableObject
{
    public HosterUploadSummary(
        string hosterName,
        string accountUsername,
        IReadOnlyList<SummaryFileItem> files,
        long? availableBytes,
        long? maxFileSize,
        FileHosterLoginDto? account = null)
    {
        HosterName = hosterName;
        AccountUsername = accountUsername;
        Account = account;
        Files = [with(files)];
        AvailableBytes = availableBytes;
        MaxFileSize = maxFileSize;

        foreach (SummaryFileItem item in Files)
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }

        Recompute();
    }

    public string HosterName { get; }

    public string AccountUsername { get; }

    public ObservableCollection<SummaryFileItem> Files { get; }

    /// <summary>Remaining free space on the selected account (quota − used), or null when the hoster
    /// reports no quota — treated as unlimited, so it never constrains and never auto-fits. Updated in
    /// place by <see cref="ApplyRefreshedAvailable"/> when a live storage refresh lands.</summary>
    public long? AvailableBytes { get; private set; }

    /// <summary>The selected account for this hoster, used to refresh its storage on the Summary page.
    /// Null for a synthetic/anonymous selection (nothing to refresh).</summary>
    public FileHosterLoginDto? Account { get; }

    /// <summary>True while a live storage refresh for this hoster is in flight — drives the per-hoster
    /// "checking available space…" indicator.</summary>
    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    /// <summary>True once the user has manually toggled a file on this hoster. A landing storage
    /// refresh then updates the available figure WITHOUT re-running the auto-fit, so it never wipes
    /// the user's own choices.</summary>
    public bool HasUserEdits { get; private set; }

    private bool _applyingAutoFit;

    /// <summary>Per-file size cap the hoster's pipeline declares, or null when it declares none.</summary>
    public long? MaxFileSize { get; }

    /// <summary>Raised when a file's Included toggle changes the included total — the wizard listens
    /// so it can re-evaluate whether Next should be blocked.</summary>
    public event EventHandler? CapacityChanged;

    /// <summary>Bytes of the currently-checked files.</summary>
    [ObservableProperty]
    public partial long IncludedBytes { get; set; }

    /// <summary>Count of currently-checked files.</summary>
    [ObservableProperty]
    public partial int IncludedCount { get; set; }

    /// <summary>True when the checked files exceed <see cref="AvailableBytes"/> (only possible for a
    /// quota-reporting hoster). Drives the red capacity line and the wizard's Next block.</summary>
    [ObservableProperty]
    public partial bool IsOverCapacity { get; set; }

    /// <summary>True when this hoster's account reports a storage quota (so capacity applies).</summary>
    public bool HasQuota => AvailableBytes is not null;

    /// <summary>Number of this hoster's eligible files currently UNchecked, for any reason (auto-fit drop
    /// OR a manual toggle).</summary>
    public int UncheckedCount => Files.Count - IncludedCount;

    /// <summary>Number of this hoster's files the capacity auto-fit unchecked FOR SPACE and are still
    /// unchecked (not since re-checked or hand-toggled). This — not <see cref="UncheckedCount"/> — drives
    /// the "unchecked to fit" clue, so a file the user unchecks by hand is not miscounted as a space
    /// eviction.</summary>
    public int SpaceUncheckedCount => Files.Count(item => !item.Included && item.AutoUncheckedForSpace);

    /// <summary>True for a quota hoster with files auto-unchecked FOR SPACE — drives a per-hoster
    /// "N unchecked to fit" hint so the user sees, on each hoster, why files were deselected there (not just
    /// the page-level banner). Never shows for an unlimited hoster (no capacity reason to deselect), for a
    /// purely hand-unchecked hoster (not a space eviction), nor while the hoster is over capacity — there the
    /// red over-capacity hint stands alone rather than pairing with an "unchecked to fit" line that would
    /// read oddly against "you're over, uncheck more".</summary>
    public bool HasUncheckedFiles => HasQuota && SpaceUncheckedCount > 0 && !IsOverCapacity;

    /// <summary>"N file(s) unchecked to fit the available space (X free)" for this hoster; empty when
    /// none. HasUncheckedFiles implies HasQuota, so AvailableBytes is always present here.</summary>
    public string UncheckedDisplay => HasUncheckedFiles && AvailableBytes is long available
        ? string.Format(
            CultureInfo.CurrentCulture,
            Localizer.Instance["Wizard_Summary_AutoFitNoticeWithFree_Format"],
            SpaceUncheckedCount,
            ByteUnit.FromBytes(available, ByteBase.Binary).ToFriendlyString())
        : string.Empty;

    // Total eligible files / bytes (independent of the checkbox state) — kept for the summary header.
    public int FileCount => Files.Count;

    public long TotalSize => Files.Sum(f => f.Size);

    /// <summary>The expander-header summary of what's CHECKED — "•  N files  •  &lt;bytes&gt;" plus the
    /// optional per-file-cap hint. A single string (not inline Runs) so it refreshes live as the user
    /// toggles files: inline <c>&lt;Run&gt;</c> text doesn't re-render on a source change.</summary>
    public string IncludedSummary
    {
        get
        {
            // Spell out both halves — "N of M files selected" and "X to upload" — so a header like
            // "0 of 54 files selected • 0 B to upload" reads unambiguously as the current selection
            // (e.g. when a full account's auto-fit unchecked everything), not "this hoster has no files".
            string filesPart = string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance["Wizard_Summary_FilesSelected_Format"],
                IncludedCount,
                FileCount);
            string sizePart = string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance["Wizard_Summary_ToUpload_Format"],
                ByteUnit.FromBytes(IncludedBytes, ByteBase.Binary).ToFriendlyString());
            return string.Format(CultureInfo.CurrentCulture, "•  {0}  •  {1}{2}", filesPart, sizePart, MaxFileSizeDisplay);
        }
    }

    /// <summary>"{checked} selected of {free} free" for a quota-reporting hoster; empty otherwise.</summary>
    public string CapacityDisplay
    {
        get
        {
            if (AvailableBytes is not long available)
            {
                return string.Empty;
            }

            string included = ByteUnit.FromBytes(IncludedBytes, ByteBase.Binary).ToFriendlyString();
            string free = ByteUnit.FromBytes(available, ByteBase.Binary).ToFriendlyString();
            return string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance["Wizard_Summary_SelectedOfFree_Format"],
                included,
                free);
        }
    }

    /// <summary>The over-capacity hint shown (red) when <see cref="IsOverCapacity"/>; empty otherwise.</summary>
    public string CapacityError => IsOverCapacity ? Localizer.Instance["Wizard_Summary_OverCapacityHint"] : string.Empty;

    /// <summary>Pre-formatted "  •  max X per file" suffix for the summary header, or empty when the
    /// hoster declares no cap.</summary>
    public string MaxFileSizeDisplay
    {
        get
        {
            if (MaxFileSize is not long bytes)
            {
                return string.Empty;
            }

            string size = ByteUnit.FromBytes(bytes, ByteBase.Binary).ToFriendlyString();
            return "  •  " + string.Format(
                CultureInfo.CurrentCulture,
                Localizer.Instance["Wizard_Summary_MaxFileSize_Format"],
                size);
        }
    }

    /// <summary>Recomputes the included total + over-capacity flag from the current checkbox states.</summary>
    public void Recompute()
    {
        long sum = 0;
        int count = 0;
        foreach (SummaryFileItem item in Files)
        {
            if (item.Included)
            {
                sum += item.Size;
                count++;
            }
        }

        IncludedBytes = sum;
        IncludedCount = count;
        IsOverCapacity = AvailableBytes is long available && sum > available;

        // CapacityDisplay/CapacityError/IncludedSummary/Unchecked* are computed off
        // IncludedBytes/IncludedCount/IsOverCapacity — nudge them so the header, capacity line and the
        // per-hoster "N unchecked" hint all refresh live as files are toggled.
        OnPropertyChanged(nameof(CapacityDisplay));
        OnPropertyChanged(nameof(CapacityError));
        OnPropertyChanged(nameof(IncludedSummary));
        OnPropertyChanged(nameof(UncheckedCount));
        OnPropertyChanged(nameof(SpaceUncheckedCount));
        OnPropertyChanged(nameof(HasUncheckedFiles));
        OnPropertyChanged(nameof(UncheckedDisplay));
    }

    /// <summary>
    /// Greedy "keep biggest that fit": for a quota-reporting hoster, walk files largest-first and keep
    /// each <see cref="SummaryFileItem.Included"/> while the running total stays within
    /// <see cref="AvailableBytes"/>; uncheck the rest. No-op for an unlimited hoster. Returns how many
    /// files THIS call unchecked — only meaningful right after construction (when every item starts
    /// checked); the wizard derives its "N unchecked to fit" notice from the final state instead.
    /// </summary>
    public int AutoFit()
    {
        if (AvailableBytes is not long available)
        {
            return 0;
        }

        // Guard so the auto-fit's own toggles don't register as user edits.
        _applyingAutoFit = true;
        try
        {
            int uncheckedCount = 0;
            long running = 0;
            foreach (SummaryFileItem item in Files.OrderByDescending(f => f.Size))
            {
                if (running + item.Size <= available)
                {
                    running += item.Size;
                    item.Included = true;
                    item.AutoUncheckedForSpace = false; // fits → not an eviction (also clears a prior re-fit drop)
                }
                else
                {
                    if (item.Included)
                    {
                        uncheckedCount++;
                    }

                    item.Included = false;
                    item.AutoUncheckedForSpace = true; // evicted for space → this is what the "unchecked to fit" notices count
                }
            }

            return uncheckedCount;
        }
        finally
        {
            _applyingAutoFit = false;
        }
    }

    /// <summary>
    /// Applies a freshly-refreshed available figure: updates <see cref="AvailableBytes"/> and, when the
    /// user hasn't manually edited this hoster yet, re-runs the auto-fit against the new number;
    /// otherwise it leaves their selection alone (the capacity line / over-capacity state still
    /// reflects the fresh figure). Raises <see cref="CapacityChanged"/> so the wizard re-evaluates Next.
    /// </summary>
    public void ApplyRefreshedAvailable(long? newAvailable)
    {
        AvailableBytes = newAvailable;
        OnPropertyChanged(nameof(AvailableBytes));
        OnPropertyChanged(nameof(HasQuota));

        if (!HasUserEdits)
        {
            AutoFit();
        }

        Recompute();
        CapacityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SummaryFileItem.Included))
        {
            // A toggle outside the auto-fit is the user's own edit — remember it so a landing storage
            // refresh respects their choices rather than re-fitting over them. It also means this file's
            // checked-state is now user-driven, so it no longer counts as a capacity eviction — otherwise a
            // hand-unchecked file is miscounted as "unchecked to fit the available space".
            if (!_applyingAutoFit)
            {
                HasUserEdits = true;
                if (sender is SummaryFileItem toggled)
                {
                    toggled.AutoUncheckedForSpace = false;
                }
            }

            Recompute();
            CapacityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
