// <copyright file="UploadWizardStepsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.Http;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Lib.Crypto;
using CSUploader.Lib.Net;
using CSUploader.Lib.Net.Http;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.ViewModels;
using CSUploader.Views;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless verification of the ported <see cref="UploadWizardWindow"/> steps 1 (hosters grid) and 3 (start
/// mode + scheduled DatePicker) — Phase 6 Task 8. The load-bearing checks:
/// <list type="bullet">
///   <item><description><b>EnumBool two-way (prep 8 / Reality-check #21)</b>: an external <c>StartMode</c>
///   change re-checks the matching RadioButton; clicking a button writes the enum; and unchecking a button
///   returns <c>BindingOperations.DoNothing</c> so the enum is never nulled — its FIRST real exercise on a
///   dedicated (non-mode) group;</description></item>
///   <item><description><b>DatePicker converter (rule 36 / prep 3)</b>: the scheduled DatePicker round-trips
///   the non-nullable <see cref="System.DateTime"/> <c>ScheduledDate</c> through <c>DateTimeOffsetConverter</c>
///   in the LIVE binding, including the null-clear DoNothing path;</description></item>
///   <item><description><b>hosters grid</b>: the <c>Use</c> checkbox two-way-writes, <c>!CanUse</c> hides the
///   checkbox and shows the account-required glyph, the "Add account…" link runs the command, and the
///   validation-warnings border follows <c>HasHosterValidationWarnings</c>.</description></item>
/// </list>
/// The wizard is NEVER driven through GoNext at step 3 (that path calls StartUploadAsync — a real upload);
/// every shown window is closed in a <c>finally</c> (headless windows are process-global).
/// </summary>
public class UploadWizardStepsTests
{
    // ── Step 3: the start-mode EnumBool group two-way, both directions + the DoNothing uncheck (prep 8) ──

    [AvaloniaFact]
    public void StartModeRadios_EnumBoolTwoWay_BothDirections_AndDoNothingUncheckKeepsTheEnum()
    {
        using VmHarness harness = new();
        harness.Vm.CurrentStep = 3;
        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // Default StartMode=Immediately → only the Immediately radio is checked.
            Assert.Equal(UploadStartMode.Immediately, harness.Vm.StartMode);
            Assert.True(wizard.ImmediatelyRadio.IsChecked);
            Assert.False(wizard.LaterRadio.IsChecked);
            Assert.False(wizard.ScheduledRadio.IsChecked);

            // External enum change re-checks the matching button and unchecks the others (Convert path).
            harness.Vm.StartMode = UploadStartMode.Scheduled;
            Dispatcher.UIThread.RunJobs();
            Assert.True(wizard.ScheduledRadio.IsChecked);
            Assert.False(wizard.ImmediatelyRadio.IsChecked);
            Assert.False(wizard.LaterRadio.IsChecked);

            // Clicking a button writes the enum (ConvertBack true path). The group unchecks the previously
            // checked Scheduled radio, whose ConvertBack(false) returns DoNothing — so StartMode stays Later,
            // it is NOT nulled by the uncheck.
            wizard.LaterRadio.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(UploadStartMode.Later, harness.Vm.StartMode);
            Assert.False(wizard.ScheduledRadio.IsChecked);
            Assert.False(wizard.ImmediatelyRadio.IsChecked);

            // Isolate the DoNothing path: directly uncheck the only-checked button. ConvertBack(false) →
            // DoNothing aborts the write, so the enum keeps its value (never nulled/reset — Reality-check #21).
            wizard.LaterRadio.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(UploadStartMode.Later, harness.Vm.StartMode);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 3: the scheduled row shows/hides with IsScheduledMode ──

    [AvaloniaFact]
    public void ScheduledRow_VisibilityFollowsIsScheduledMode()
    {
        using VmHarness harness = new();
        harness.Vm.CurrentStep = 3;
        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // Immediately (default) → the scheduled DatePicker row is hidden.
            Assert.False(harness.Vm.IsScheduledMode);
            Assert.False(wizard.ScheduledRow.IsVisible);

            harness.Vm.StartMode = UploadStartMode.Scheduled;
            Dispatcher.UIThread.RunJobs();
            Assert.True(wizard.ScheduledRow.IsVisible);

            harness.Vm.StartMode = UploadStartMode.Later;
            Dispatcher.UIThread.RunJobs();
            Assert.False(wizard.ScheduledRow.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 3: the DatePicker round-trips ScheduledDate through DateTimeOffsetConverter (rule 36) ──

    [AvaloniaFact]
    public void ScheduledDatePicker_RoundTripsScheduledDate_ThroughTheConverter_AndNullClearIsANoOp()
    {
        using VmHarness harness = new();
        harness.Vm.StartMode = UploadStartMode.Scheduled;
        harness.Vm.CurrentStep = 3;
        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // Convert: the VM's non-nullable DateTime seeds the DateTimeOffset? picker (same calendar day).
            Assert.NotNull(wizard.ScheduledDatePicker.SelectedDate);
            Assert.Equal(harness.Vm.ScheduledDate.Date, wizard.ScheduledDatePicker.SelectedDate!.Value.Date);

            // ConvertBack: picking a date writes it back through the converter to the non-nullable DateTime.
            var picked = new DateTimeOffset(new DateTime(2027, 3, 15, 0, 0, 0, DateTimeKind.Unspecified));
            wizard.ScheduledDatePicker.SelectedDate = picked;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new DateTime(2027, 3, 15), harness.Vm.ScheduledDate.Date);

            // Null clear: ConvertBack(null) → DoNothing, so a cleared picker leaves ScheduledDate untouched
            // (it must not zero the non-nullable default) — the DoNothing sentinel proven in the live binding.
            wizard.ScheduledDatePicker.SelectedDate = null;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new DateTime(2027, 3, 15), harness.Vm.ScheduledDate.Date);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the Use checkbox two-way-writes; !CanUse gates the checkbox vs the account-required glyph ──

    [AvaloniaFact]
    public void HostersGrid_UseCheckboxTwoWay_AndCanUseGatesCheckboxVsGlyph()
    {
        using VmHarness harness = new();
        FileHosterSelectionViewModel usable = new("Catbox", [new FileHosterLoginDto { FileHosterName = "Catbox", Username = "me" }]);
        FileHosterSelectionViewModel blocked = new("Nowhere", []);
        harness.Vm.Hosters.FileHosters.Add(usable);
        harness.Vm.Hosters.FileHosters.Add(blocked);
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // The grid reads a filtered VIEW over the collection, not the collection itself — that is
            // what lets a ticked hoster stay in the upload after the filter hides it. The view's source
            // is still the VM's list.
            DataGridCollectionView view = Assert.IsType<DataGridCollectionView>(wizard.fileHostersGrid.ItemsSource);
            Assert.Same(harness.Vm.Hosters.FileHosters, view.SourceCollection);

            DataGridRow usableRow = RowFor(wizard.fileHostersGrid, usable);
            DataGridRow blockedRow = RowFor(wizard.fileHostersGrid, blocked);

            // Usable row: the enable checkbox shows, the account-required glyph is hidden.
            CheckBox usableCheck = CheckboxIn(usableRow);
            Assert.True(usableCheck.IsVisible);
            Assert.False(GlyphIn(usableRow).IsVisible);

            // Toggling the checkbox two-way-writes Use onto the row VM.
            usableCheck.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(usable.Use);

            // Blocked row (no accounts, no anonymous): the checkbox is hidden, the glyph shows.
            Assert.False(CheckboxIn(blockedRow).IsVisible);
            Assert.True(GlyphIn(blockedRow).IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the Use column's header box ticks everything the filter leaves listed ──

    [AvaloniaFact]
    public void HostersGrid_HeaderCheckBox_TicksEveryListedHoster()
    {
        using VmHarness harness = new();
        FileHosterSelectionViewModel catbox = new("Catbox", [], supportsAnonymous: true);
        FileHosterSelectionViewModel rapidgator = new("Rapidgator", [new FileHosterLoginDto { FileHosterName = "Rapidgator", Username = "me" }]);
        harness.Vm.Hosters.FileHosters.Add(catbox);
        harness.Vm.Hosters.FileHosters.Add(rapidgator);
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // The header content is the box itself; its binding reaches the WINDOW's DataContext,
            // because a column header has no row behind it — the part most likely to be silently
            // wrong, and a silent failure here looks like a dead control.
            CheckBox header = wizard.fileHostersGrid.Columns[0].Header as CheckBox
                ?? throw new InvalidOperationException("the Use column header is not a CheckBox");
            Assert.True(header.IsThreeState);
            Assert.False(header.IsChecked);

            header.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(catbox.Use);
            Assert.True(rapidgator.Use);

            // Untick one row and the header reads partial rather than stale.
            catbox.Use = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(header.IsChecked);

            // Filtering changes what "all" means: only the listed row is ticked.
            harness.Vm.Hosters.AccountFilter = HosterAccountFilter.AnonymousOnly;
            Dispatcher.UIThread.RunJobs();
            header.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(catbox.Use);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: Next is disabled, and says why, until a hoster is ticked ──

    [AvaloniaFact]
    public void HostersGrid_NextStaysDisabledWithAHint_UntilAHosterIsTicked()
    {
        using VmHarness harness = new();
        FileHosterSelectionViewModel catbox = new("Catbox", [], supportsAnonymous: true);
        harness.Vm.Hosters.FileHosters.Add(catbox);
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // Nothing ticked: the button is off and the hint explains it — a disabled control with no
            // explanation is the part users report as broken.
            Assert.False(wizard.NextButton.IsEnabled);
            Assert.True(wizard.PickAHosterHint.IsVisible);

            catbox.Use = true;
            Dispatcher.UIThread.RunJobs();

            Assert.True(wizard.NextButton.IsEnabled);
            Assert.False(wizard.PickAHosterHint.IsVisible);

            catbox.Use = false;
            Dispatcher.UIThread.RunJobs();

            Assert.False(wizard.NextButton.IsEnabled);
            Assert.True(wizard.PickAHosterHint.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the filter bar narrows the GRID without touching the list the upload is built from ──

    [AvaloniaFact]
    public void HostersGrid_FilterBar_NarrowsTheGrid_AndKeepsTickedHostersInTheUpload()
    {
        using VmHarness harness = new();
        FileHosterSelectionViewModel catbox = new("Catbox", [], supportsAnonymous: true);
        FileHosterSelectionViewModel rapidgator = new("Rapidgator", [new FileHosterLoginDto { FileHosterName = "Rapidgator", Username = "me" }]);
        harness.Vm.Hosters.FileHosters.Add(catbox);
        harness.Vm.Hosters.FileHosters.Add(rapidgator);
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            DataGridCollectionView view = Assert.IsType<DataGridCollectionView>(wizard.fileHostersGrid.ItemsSource);
            Assert.Equal(2, view.Count);

            // Tick a hoster, THEN filter it away: the grid stops showing it and the row keeps its tick,
            // because the filter is a view over the collection rather than a rewrite of it.
            catbox.Use = true;
            harness.Vm.Hosters.HosterFilterText = "rapid";
            Dispatcher.UIThread.RunJobs();

            Assert.Single(view);
            Assert.DoesNotContain(catbox, view.Cast<object>());
            Assert.Contains(catbox, harness.Vm.Hosters.FileHosters);
            Assert.True(catbox.Use);

            // Anonymous-only swaps which one is left — Rapidgator needs an account.
            harness.Vm.Hosters.HosterFilterText = string.Empty;
            harness.Vm.Hosters.AccountFilter = HosterAccountFilter.AnonymousOnly;
            Dispatcher.UIThread.RunJobs();

            Assert.Single(view);
            Assert.Contains(catbox, view.Cast<object>());

            // Clearing restores the whole list.
            harness.Vm.Hosters.ClearHosterFilterCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, view.Count);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the captcha-free filter checkbox is wired to the grid ──

    [AvaloniaFact]
    public void HostersFilterBar_NoCaptchaCheckbox_FiltersTheGridToVerifiedCaptchaFreeHosters()
    {
        using VmHarness harness = new();
        FileHosterSelectionViewModel catbox = new(
            "Catbox",
            [],
            supportsAnonymous: true,
            downloadCaptcha: CSUploader.Upload.Pipeline.DownloadCaptchaRequirement.NotRequired);
        FileHosterSelectionViewModel rapidgator = new(
            "Rapidgator",
            [new FileHosterLoginDto { FileHosterName = "Rapidgator", Username = "me" }],
            downloadCaptcha: CSUploader.Upload.Pipeline.DownloadCaptchaRequirement.Required);
        FileHosterSelectionViewModel unverified = new(
            "Xubster",
            [new FileHosterLoginDto { FileHosterName = "Xubster", Username = "me" }],
            downloadCaptcha: CSUploader.Upload.Pipeline.DownloadCaptchaRequirement.Unknown);
        harness.Vm.Hosters.FileHosters.Add(catbox);
        harness.Vm.Hosters.FileHosters.Add(rapidgator);
        harness.Vm.Hosters.FileHosters.Add(unverified);
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            CheckBox box = wizard.GetVisualDescendants().OfType<CheckBox>().First(
                c => Equals(c.Content, CSUploader.Lib.Localization.Localizer.Instance["Wizard_Step2_FilterNoCaptcha"]));
            DataGridCollectionView view = Assert.IsType<DataGridCollectionView>(wizard.fileHostersGrid.ItemsSource);
            Assert.Equal(3, view.Count);

            box.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            // Ticking the box drives the VM flag, and the grid's view re-filters to the one hoster
            // whose downloads were VERIFIED captcha-free — the unverified dash is hidden, not kept.
            Assert.True(harness.Vm.Hosters.NoDownloadCaptchaOnly);
            Assert.Single(view);
            Assert.Contains(catbox, view.Cast<object>());
            Assert.DoesNotContain(unverified, view.Cast<object>());

            // …and the row filtered out of sight keeps whatever tick it had: the filter is a view.
            Assert.Contains(rapidgator, harness.Vm.Hosters.FileHosters);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the two cap columns render the row's text, including "No limit" for the common case ──

    [AvaloniaFact]
    public void HostersGrid_ShowsMaxFileSizeAndMaxParallelColumns()
    {
        using VmHarness harness = new();
        FileHosterSelectionViewModel capped = new(
            "DropMeFiles",
            [],
            supportsAnonymous: true,
            maxFileSizeResolver: _ => 53_687_091_200,
            maxConcurrentResolver: _ => 5);
        FileHosterSelectionViewModel uncapped = new(
            "Catbox",
            [new FileHosterLoginDto { FileHosterName = "Catbox", Username = "me" }],
            maxFileSizeResolver: _ => null,
            maxConcurrentResolver: _ => null);
        harness.Vm.Hosters.FileHosters.Add(capped);
        harness.Vm.Hosters.FileHosters.Add(uncapped);
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            string noLimit = CSUploader.Lib.Localization.Localizer.Instance["Wizard_Step2_NoLimit"];

            List<string?> cappedTexts = RowFor(wizard.fileHostersGrid, capped)
                .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            List<string?> uncappedTexts = RowFor(wizard.fileHostersGrid, uncapped)
                .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

            Assert.Contains("5", cappedTexts);                 // the hoster's own parallel ceiling
            Assert.Contains("50 GiB", cappedTexts);            // …alongside its size cap
            Assert.Equal(2, uncappedTexts.Count(t => t == noLimit)); // both columns read "No limit"
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the "Kept for" dash explains itself, and from the whole cell, not the glyph ──

    [AvaloniaFact]
    public void HostersGrid_KeptForDash_CarriesItsTooltipOnTheWholeCell()
    {
        using VmHarness harness = new();
        FileHosterSelectionViewModel unknown = new(
            "Rapidgator",
            [new FileHosterLoginDto { FileHosterName = "Rapidgator", Username = "me" }],
            retentionResolver: _ => CSUploader.Upload.Pipeline.FileRetention.Unspecified);
        harness.Vm.Hosters.FileHosters.Add(unknown);
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            string dash = CSUploader.Lib.Localization.Localizer.Instance["Wizard_Step2_Retention_Unknown"];
            string explain = CSUploader.Lib.Localization.Localizer.Instance["Wizard_Step2_Retention_UnknownTooltip"];

            TextBlock dashText = RowFor(wizard.fileHostersGrid, unknown)
                .GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == dash);

            // The tooltip must sit on the cell-filling Border ABOVE the TextBlock: an em dash is a
            // few pixels of glyph, so the glyph itself is not a hover target anyone can hit.
            Border cell = Assert.IsType<Border>(dashText.GetVisualParent());
            Assert.Equal(explain, ToolTip.GetTip(cell));
            Assert.True(cell.Bounds.Width > dashText.Bounds.Width * 2);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: hoster columns resize like every other grid's; the control strips don't ──

    [AvaloniaFact]
    public void HostersGrid_ColumnsAreUserResizable_ExceptControlStrips()
    {
        using VmHarness harness = new();
        harness.Vm.Hosters.FileHosters.Add(new FileHosterSelectionViewModel("Catbox", [], supportsAnonymous: true));
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            Assert.True(wizard.fileHostersGrid.CanUserResizeColumns);

            // The Use checkbox column and the scrollbar-gutter strip are fixed furniture, not data —
            // dragging either to nothing (or to half the grid) helps nobody. Every DATA column in
            // between must stay draggable.
            Assert.False(wizard.fileHostersGrid.Columns[0].CanUserResize);
            Assert.False(wizard.fileHostersGrid.Columns[^1].CanUserResize);
            foreach (DataGridColumn dataColumn in wizard.fileHostersGrid.Columns.Skip(1).Take(wizard.fileHostersGrid.Columns.Count - 2))
            {
                Assert.True(dataColumn.CanUserResize);
            }

            // The three slim capability headers ("Max file size", "Max parallel", "Kept for") wrap
            // instead of clipping: the columns are sized for their VALUES, and several locales'
            // labels outgrow that — initial readability must not depend on discovering the resize
            // grip. (The captcha header's wrap is pinned by its own test below.)
            foreach (int slimColumn in (int[])[3, 4, 5])
            {
                TextBlock slimHeader = Assert.IsType<TextBlock>(wizard.fileHostersGrid.Columns[slimColumn].Header);
                Assert.Equal(global::Avalonia.Media.TextWrapping.Wrap, slimHeader.TextWrapping);
            }
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the "Download captcha?" column renders the verdict; its dash explains itself ──

    [AvaloniaFact]
    public void HostersGrid_DownloadCaptchaColumn_ShowsVerdictAndExplainsTheDash()
    {
        using VmHarness harness = new();
        FileHosterSelectionViewModel gated = new(
            "Rapidgator",
            [new FileHosterLoginDto { FileHosterName = "Rapidgator", Username = "me" }],
            downloadCaptcha: CSUploader.Upload.Pipeline.DownloadCaptchaRequirement.Required);
        FileHosterSelectionViewModel unverified = new(
            "Hxfile",
            [new FileHosterLoginDto { FileHosterName = "Hxfile", Username = "me" }],
            downloadCaptcha: CSUploader.Upload.Pipeline.DownloadCaptchaRequirement.Unknown);
        harness.Vm.Hosters.FileHosters.Add(gated);
        harness.Vm.Hosters.FileHosters.Add(unverified);
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            string yes = CSUploader.Lib.Localization.Localizer.Instance["Common_Yes"];
            string dash = CSUploader.Lib.Localization.Localizer.Instance["Wizard_Step2_Captcha_Unknown"];
            string explain = CSUploader.Lib.Localization.Localizer.Instance["Wizard_Step2_Captcha_UnknownTooltip"];

            TextBlock yesText = RowFor(wizard.fileHostersGrid, gated)
                .GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == yes);

            // Centred, not right-aligned like the numeric columns: categorical text.
            Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Center, yesText.HorizontalAlignment);

            // The dash's tooltip must ride the cell-filling Border, not the few-pixel glyph —
            // same rationale as the "Kept for" dash above.
            TextBlock dashText = RowFor(wizard.fileHostersGrid, unverified)
                .GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == dash);
            Border cell = Assert.IsType<Border>(dashText.GetVisualParent());
            Assert.Equal(explain, ToolTip.GetTip(cell));
            Assert.True(cell.Bounds.Width > dashText.Bounds.Width * 2);

            // Column-level seams nothing else pins: the stringly-typed sort path, the clipboard
            // binding, and the header (its definition tooltip + wrapping — "Download captcha?"
            // fits 110px in only some of the six languages). [^2] = the last real column, ahead
            // of the scrollbar-gutter strip that must stay last.
            DataGridColumn captchaColumn = wizard.fileHostersGrid.Columns[^2];
            Assert.Equal(nameof(FileHosterSelectionViewModel.DownloadCaptchaSortKey), captchaColumn.SortMemberPath);
            // Compiled bindings are this project's default, so the XAML produces a
            // CompiledBindingExtension, not a reflection Binding.
            var clipboard = Assert.IsType<global::Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindingExtension>(
                captchaColumn.ClipboardContentBinding);
            Assert.Equal(nameof(FileHosterSelectionViewModel.DownloadCaptchaDisplay), clipboard.Path.ToString());
            TextBlock header = Assert.IsType<TextBlock>(captchaColumn.Header);
            Assert.Equal(CSUploader.Lib.Localization.Localizer.Instance["Wizard_Step2_Col_Captcha"], header.Text);
            Assert.Equal(
                CSUploader.Lib.Localization.Localizer.Instance["Wizard_Step2_Col_Captcha_Tooltip"],
                ToolTip.GetTip(header));
            Assert.Equal(global::Avalonia.Media.TextWrapping.Wrap, header.TextWrapping);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the "Add account…" link runs AddAccountForHosterCommand for its row ──

    [AvaloniaFact]
    public void AddAccountLink_RunsAddAccountForHosterCommand_ForItsRow()
    {
        Mock<IDialogService> dialog = new();
        dialog
            .Setup(d => d.ShowAddAccountDialogAsync(
                It.IsAny<string>(),
                It.IsAny<string[]>(),
                It.IsAny<Func<string, Task<AccountCheckResult>>>(),
                It.IsAny<string?>()))
            .ReturnsAsync((FileHosterLoginDto?)null);

        using VmHarness harness = new(dialog.Object);
        FileHosterSelectionViewModel blocked = new("Nowhere", []);
        harness.Vm.Hosters.FileHosters.Add(blocked);
        harness.Vm.CurrentStep = 1;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            // The code-behind seam the link's left-button PointerReleased delegates to (the sanctioned
            // fallback for a cell-template link — a real pointer release can't be synthesized headless).
            wizard.InvokeAddAccountForHoster(blocked);
            Dispatcher.UIThread.RunJobs();

            dialog.Verify(
                d => d.ShowAddAccountDialogAsync(
                    "Nowhere",
                    It.IsAny<string[]>(),
                    It.IsAny<Func<string, Task<AccountCheckResult>>>(),
                    It.IsAny<string?>()),
                Times.Once);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Step 1: the validation-warnings border follows HasHosterValidationWarnings (rule 33) ──

    [AvaloniaFact]
    public void HosterWarningsBorder_VisibilityFollowsHasHosterValidationWarnings()
    {
        // No warnings → the border is hidden.
        using VmHarness empty = new();
        empty.Vm.CurrentStep = 1;
        (Window emptyWindow, UploadWizardWindow emptyWizard) = Show(empty.Vm);

        // A pre-seeded warning → the border is visible (HasHosterValidationWarnings reads Count>0 at bind).
        using VmHarness warned = new();
        warned.Vm.Hosters.HosterValidationWarnings.Add("Catbox: 3 files exceed the size limit");
        warned.Vm.CurrentStep = 1;
        (Window warnedWindow, UploadWizardWindow warnedWizard) = Show(warned.Vm);
        try
        {
            Assert.False(emptyWizard.HosterWarningsBorder.IsVisible);
            Assert.True(warnedWizard.HosterWarningsBorder.IsVisible);
        }
        finally
        {
            emptyWindow.Close();
            warnedWindow.Close();
        }
    }

    // ── helpers ──

    private static DataGridRow RowFor(DataGrid grid, FileHosterSelectionViewModel row)
        => grid.GetVisualDescendants().OfType<DataGridRow>().First(r => ReferenceEquals(r.DataContext, row));

    private static CheckBox CheckboxIn(DataGridRow row)
        => row.GetVisualDescendants().OfType<CheckBox>().First();

    // The account-required indicator in the Use column: a named vector padlock Path (was a Segoe MDL2
    // glyph). Named so it is not confused with the CheckBox template's own checkmark Path in the row.
    private static Control GlyphIn(DataGridRow row)
        => row.GetVisualDescendants().OfType<global::Avalonia.Controls.Shapes.Path>().First(p => p.Name == "UseLockGlyph");

    // ── Step 0: the drop hint is withheld where the platform cannot deliver a drop ──

    [AvaloniaFact]
    public void DropHint_IsHidden_WhereThePlatformCannotDeliverADrop()
    {
        // Avalonia's X11 backend implements no XDND, so a Linux drop never arrives and the hint
        // promises something that silently does nothing — which is how it got reported as a bug.
        // Forced rather than read from the OS: this suite only ever runs on Windows, where the real
        // value is true, so the hidden case — the actual fix — would otherwise go unasserted.
        using VmHarness harness = new();
        harness.Vm.Sources.SupportsFileDrop = false;
        harness.Vm.CurrentStep = 0;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            Assert.False(wizard.DropHint.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DropHint_IsShown_WhereDropsDoArrive()
    {
        // The other half: the binding must not simply hide it always, which would "pass" the test
        // above while removing the affordance from Windows and macOS too.
        using VmHarness harness = new();
        harness.Vm.Sources.SupportsFileDrop = true;
        harness.Vm.CurrentStep = 0;

        (Window window, UploadWizardWindow wizard) = Show(harness.Vm);
        try
        {
            Assert.True(wizard.DropHint.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static (Window Window, UploadWizardWindow Wizard) Show(UploadWizardViewModel vm)
    {
        UploadWizardWindow wizard = new(vm);
        wizard.Show();
        Dispatcher.UIThread.RunJobs();
        return (wizard, wizard);
    }

    /// <summary>
    /// A real <see cref="UploadWizardViewModel"/> over an in-memory SQLite DB — the same scratch-repo harness
    /// the shell tests use (the WPF <c>UploadWizardViewModelTests</c> shape). Steps 1/3 are exercised by
    /// direct property/collection sets, never through GoNext, so the PackageManager only satisfies the ctor.
    /// </summary>
    private sealed class VmHarness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly UploadScheduler _scheduler;

        public VmHarness(IDialogService? dialog = null)
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();

            DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
                .UseSqlite(_connection)
                .Options;
            TestDbContextFactory factory = new(options);
            using (CSUploaderDbContext db = factory.CreateDbContext())
            {
                db.Database.EnsureCreated();
            }

            FileHosterLoginRepository loginRepo = new(factory);
            AppSettings settings = new();
            DefaultFileHosterRegistry registry = new([]);
            _scheduler = new UploadScheduler(settings, BuildAttemptRunner(), Mock.Of<IAppLogger>(), new HashingService(), registry);
            PackageManager packageManager = new(
                settings,
                _scheduler,
                new UploadPackageRepository(factory),
                new UploadPackageFileRepository(factory),
                loginRepo,
                Mock.Of<IAppLogger>(),
                registry);

            Vm = new UploadWizardViewModel(packageManager, loginRepo, dialog ?? Mock.Of<IDialogService>(), Mock.Of<IAppLogger>(), settings);
        }

        public UploadWizardViewModel Vm { get; }

        public void Dispose()
        {
            _scheduler.Dispose();
            _connection.Dispose();
        }

        private static AttemptRunner BuildAttemptRunner()
        {
            DefaultFileHosterRegistry registry = new([]);
            Mock<IProxySource> proxy = new();
            proxy.Setup(p => p.Next()).Returns(ProxyChoice.Direct);
            Mock<IHttpHandlerFactory> hf = new();
            hf.Setup(f => f.Create(It.IsAny<ProxyChoice>(), It.IsAny<IAppLogger>()))
                .Returns(new HttpHandler(new HttpClient(), Mock.Of<IAppLogger>(), null, MockServerConfig.Disabled));
            return new AttemptRunner(registry, proxy.Object, hf.Object);
        }

        private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
            : IDbContextFactory<CSUploaderDbContext>
        {
            public CSUploaderDbContext CreateDbContext() => new(options);
        }
    }
}
