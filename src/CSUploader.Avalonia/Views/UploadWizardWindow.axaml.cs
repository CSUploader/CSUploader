// <copyright file="UploadWizardWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CSUploader.Dal;
using CSUploader.Lib;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Upload.Pipeline;
using CSUploader.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.Views;

/// <summary>
/// The Upload Wizard window (Avalonia port of the WPF <c>UploadWizardWindow</c>). Task 7 lands the shell,
/// the step indicator, the 4-step nav bar and step 0 (the merged source-picker + files grid); steps 1-3
/// follow in Tasks 8-9. Two head-specific pieces this task establishes:
/// <list type="bullet">
///   <item><description>the VM is hand-built from <see cref="App.Services"/> — <see cref="UploadWizardViewModel"/>
///   is NOT DI-registered, so the production ctor resolves its seven dependencies exactly as the WPF ctor does;</description></item>
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
    private readonly UploadWizardViewModel _vm;

    /// <summary>
    /// The hand-built wizard VM. Internal so the gallery dev surface can seed a sample
    /// <see cref="UploadWizardViewModel.DirectoryPath"/> before showing (the agent bridge can't commit the
    /// LostFocus-bound directory box, so the gallery pre-loads a fake directory to exercise steps 0-1).
    /// </summary>
    internal UploadWizardViewModel ViewModel => _vm;

    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001); the app always uses the
    // injecting (UploadsViewModel) overload via compiled XAML. It routes through the same App.Services hand-
    // build, so it only functions with a live desktop provider — never invoked in production or the headless
    // suite (which use the injecting / internal overloads).
    public UploadWizardWindow()
        : this(BuildViewModel())
    {
    }

    /// <summary>
    /// Production ctor — mirrors the WPF <c>(UploadsViewModel uploadsVm)</c> shape. <paramref name="uploadsVm"/>
    /// is vestigial (see the type remarks): it is accepted for call-site parity and never used. Resolves the
    /// seven services from <see cref="App.Services"/> and hand-builds the (non-DI-registered) VM.
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
        _vm = vm;
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
            Command = _vm.RemoveSelectedFilesCommand,
            CommandParameter = filesGrid.SelectedItems,
        });

        _vm.PropertyChanged += Vm_PropertyChanged;
        DataContext = _vm;
    }

    private static UploadWizardViewModel BuildViewModel()
    {
        IServiceProvider sp = ((App)Application.Current!).Services;
        return new UploadWizardViewModel(
            sp.GetRequiredService<PackageManager>(),
            sp.GetRequiredService<FileHosterLoginRepository>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IAppLogger>(),
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<IFileHosterRegistry>(),
            sp.GetRequiredService<IAccountVerifier>());
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UploadWizardViewModel.Completed) && _vm.Completed)
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
        if (_vm.AddAccountForHosterCommand.CanExecute(hoster))
        {
            _vm.AddAccountForHosterCommand.Execute(hoster);
        }
    }
}
