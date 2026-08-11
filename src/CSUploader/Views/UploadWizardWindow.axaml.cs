// <copyright file="UploadWizardWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CSUploader.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.Views;

/// <summary>
/// The Upload Wizard window (Avalonia port of the WPF <c>UploadWizardWindow</c>). Task 7 lands the shell,
/// the step indicator, the 4-step nav bar and step 0 (the merged source-picker + files grid); steps 1-3
/// follow in Tasks 8-9. Two head-specific pieces this task establishes:
/// <list type="bullet">
///   <item><description>the VM is resolved from <see cref="App.Services"/> — <see cref="UploadWizardViewModel"/>
///   is DI-registered (Transient, Phase 9 ledger fix d), so each open gets a fresh wizard with the real graph;</description></item>
///   <item><description>the <c>uploadsVm</c> ctor parameter is VESTIGIAL — accepted only for call-site parity
///   with UploadsView's Add button (Task 12), never used in the body (the WPF ctor accepts and ignores it too).</description></item>
/// </list>
/// The headless suite uses the internal VM-injection ctor (App.Services is unset under the test lifetime).
/// On <see cref="UploadWizardViewModel.Completed"/> the window closes with a truthy dialog result (mirrors the
/// WPF <c>DialogResult=true</c>); Cancel/Esc close with a non-completed result (rule 7 — <c>IsCancel</c> no
/// longer auto-closes on Avalonia).
/// </summary>
public partial class UploadWizardWindow : Window
{
    /// <summary>
    /// The wizard VM (resolved from DI, or injected in the headless suite). Internal so the gallery dev surface can seed a sample
    /// <see cref="UploadWizardViewModel.DirectoryPath"/> before showing (the agent bridge can't commit the
    /// LostFocus-bound directory box, so the gallery pre-loads a fake directory to exercise steps 0-1).
    /// </summary>
    internal UploadWizardViewModel ViewModel { get; }

    /// <summary>
    /// The File Hosters grid's filtered view over <see cref="UploadWizardViewModel.FileHosters"/>.
    /// Built ONCE (a <see cref="DataGridCollectionView"/> subscribes to its source's CollectionChanged
    /// in the ctor and never unsubscribes, so re-minting one per filter keystroke would orphan the
    /// old handler) and refreshed in place — the same arrangement UploadsView uses for its filter bar.
    /// <para>
    /// Filtering here rather than in the collection is what keeps a ticked-then-hidden hoster in the
    /// upload: the VM's <see cref="UploadWizardViewModel.FileHosters"/> is untouched, and it is what
    /// the Next step reads.
    /// </para>
    /// </summary>
    internal DataGridCollectionView? HostersView { get; private set; }

    /// <summary>The files grid's filtered view over <see cref="UploadWizardViewModel.Files"/> —
    /// the tree selection and the text filter both narrow it. Built once, like
    /// <see cref="HostersView"/>.</summary>
    internal DataGridCollectionView? FilesView { get; private set; }

    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001); the app always uses the
    // injecting (UploadsViewModel) overload via compiled XAML. It routes through the same App.Services
    // resolution, so it only functions with a live desktop provider — never invoked in production or the headless
    // suite (which use the injecting / internal overloads).
    public UploadWizardWindow()
        : this(BuildViewModel())
    {
    }

    /// <summary>
    /// Production ctor — mirrors the WPF <c>(UploadsViewModel uploadsVm)</c> shape. <paramref name="uploadsVm"/>
    /// is vestigial (see the type remarks): it is accepted for call-site parity and never used. Resolves the
    /// DI-registered VM from <see cref="App.Services"/>.
    /// </summary>
    public UploadWizardWindow(UploadsViewModel uploadsVm)
        : this(BuildViewModel())
    {
    }

    /// <summary>
    /// VM-injection ctor for the headless suite (and any caller that already holds a VM). The production ctor's
    /// <see cref="App.Services"/> resolution can't run under the test lifetime, so tests build a scratch-repo VM
    /// (the WPF <c>UploadWizardViewModelTests</c> harness) and hand it here directly.
    /// </summary>
    internal UploadWizardWindow(UploadWizardViewModel vm)
    {
        ViewModel = vm;
        InitializeComponent();

        // Rule 19: DataGrid.SelectedItems is a plain IList (not a bindable AvaloniaProperty), so the bulk-
        // Remove parameter and the Delete key binding take the grid's live SelectedItems from code-behind.
        // The reference is stable for the control's lifetime — what the WPF ElementName binding resolved to.
        RemoveFilesButton.CommandParameter = filesGrid.SelectedItems;

        // Rule 24: KeyBinding is a non-DataContext AvaloniaObject, so the Delete shortcut is wired here where
        // the VM command and the SelectedItems are both in hand (mirrors the WPF DataGrid.InputBindings entry).
        filesGrid.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.Delete),
            Command = ViewModel.RemoveSelectedFilesCommand,
            CommandParameter = filesGrid.SelectedItems,
        });

        // The files grid reads a filtered VIEW rather than hiding rows in place. Collapsing a
        // DataGridRow leaves a zero-height row inside the presenter's layout, and one re-shown after
        // being collapsed could be drawn over its neighbour — two files reappearing from a
        // de-selected folder looked exactly like that on screen. A view simply doesn't contain them.
        FilesView = new DataGridCollectionView(ViewModel.Files)
        {
            Filter = ViewModel.MatchesFileFilter,
        };
        filesGrid.ItemsSource = FilesView;
        ViewModel.FileFilterInvalidated += Vm_FileFilterInvalidated;

        // The hoster grid reads a filtered view, not the raw collection (see HostersView).
        HostersView = new DataGridCollectionView(ViewModel.FileHosters)
        {
            Filter = ViewModel.MatchesHosterFilter,
        };
        fileHostersGrid.ItemsSource = HostersView;
        ViewModel.HosterFilterInvalidated += Vm_HosterFilterInvalidated;

        WireDragAndDrop();

        ViewModel.PropertyChanged += Vm_PropertyChanged;
        DataContext = ViewModel;
    }

    private void Vm_HosterFilterInvalidated(object? sender, EventArgs e) => HostersView?.Refresh();

    private void Vm_FileFilterInvalidated(object? sender, EventArgs e) => FilesView?.Refresh();

    /// <summary>
    /// Files and folders dropped anywhere on the wizard are added exactly as the two Add buttons add
    /// them — same append, same dedupe, same source rows.
    /// <para>
    /// Wired in code-behind rather than XAML because <c>DragDrop.DropEvent</c> is a routed event with
    /// no bindable property, and the VM must stay framework-free: the head converts the platform's
    /// <see cref="IStorageItem"/> list into plain paths and hands those over.
    /// </para>
    /// <para>
    /// The drop is only offered while step 0 is showing. Dropping onto a later step would add files
    /// the user cannot see (the grid is on step 0), which reads as nothing happening.
    /// </para>
    /// </summary>
    private void WireDragAndDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = ViewModel.CurrentStep == 0 && e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ViewModel.CurrentStep != 0)
        {
            return;
        }

        // DataTransfer/DataFormat, not the obsolete Data/DataFormats pair (11.3 deprecated both).
        // TryGetFiles yields IStorageItem — FOLDERS included, which is the half that matters here.
        // TryGetLocalPath() returns null for an item with no filesystem path (a virtual/provider
        // item); the VM ignores paths that are neither a file nor a folder, so those fall away.
        string[] paths = [.. (e.DataTransfer.TryGetFiles() ?? [])
            .Select(item => item.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))!];

        if (paths.Length > 0)
        {
            ViewModel.AddDroppedPaths(paths);
        }
    }


    private static UploadWizardViewModel BuildViewModel()
        => ((App)Application.Current!).Services.GetRequiredService<UploadWizardViewModel>();

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UploadWizardViewModel.Completed) && ViewModel.Completed)
        {
            // Mirror the WPF DialogResult=true; the opener (Task 12) only checks that it wasn't cancelled.
            Close(true);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    // Rule 28/10: step 1's "Add account…" affordance is a Classes="link" TextBlock (not a WPF Hyperlink), so
    // the click runs the VM command from code-behind on a LEFT-button release only. The link only renders for
    // a !CanUse row, whose DataContext is that row's FileHosterSelectionViewModel — the exact command argument.
    private void AddAccountLink_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (sender is Control { DataContext: FileHosterSelectionViewModel hoster })
        {
            InvokeAddAccountForHoster(hoster);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Runs <see cref="UploadWizardViewModel.AddAccountForHosterCommand"/> for a hoster row. Internal so the
    /// headless suite can verify the step-1 link's command wiring without synthesizing a real left-button
    /// pointer release on a cell-template TextBlock (the sanctioned fallback the EditAccountWindow Details
    /// link uses). The async command gates re-entrancy via its own CanExecute while the dialog is open.
    /// </summary>
    internal void InvokeAddAccountForHoster(FileHosterSelectionViewModel hoster)
    {
        if (ViewModel.AddAccountForHosterCommand.CanExecute(hoster))
        {
            ViewModel.AddAccountForHosterCommand.Execute(hoster);
        }
    }
}
