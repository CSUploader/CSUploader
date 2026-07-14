// <copyright file="ToastWindowTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia; // PixelPoint
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CSUploader.Services;
using CSUploader.ViewModels;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless tests for the Phase 7 Task 2 completion toast (<see cref="ToastWindow"/>,
/// <see cref="AvaloniaToastWindowFactory"/>, <see cref="AvaloniaToastHost"/> — the DIP&lt;-&gt;physical
/// adapter). The load-bearing checks are: the factory builds a host whose <see cref="IToastHost.Height"/>
/// is the window's fixed DIP height (what the service stacks from); the close button / body-click route to
/// the VM's CloseCommand / ActivateCommand; the auto-dismiss timer arms on open, pauses on hover, resumes
/// on leave and stops on close (proved without a 5s wall-clock wait — headless does not advance
/// DispatcherTimer virtual time); and the host's Top/Left DIP setters write through to Window.Position via
/// <c>ToastPlacement</c> (headless has no desktop lifetime, so the primary-screen scaling resolves to 1.0
/// and DIP == physical — the delegation is what's asserted). Every shown window is closed in a
/// <c>finally</c> (headless windows are process-global for the session).
/// </summary>
public class ToastWindowTests
{
    private static ToastViewModel MakeVm(Action? activate = null, Action? close = null)
        => new(new RelayCommand(activate ?? (() => { })), new RelayCommand(close ?? (() => { })))
        {
            Title = "Upload finished",
            Message = "holiday_clip.mkv",
            IconKey = "StatusSuccessImage",
        };

    [AvaloniaFact]
    public void Factory_CreatesHost_HeightIsWindowDipHeight()
    {
        IToastHost host = new AvaloniaToastWindowFactory().Create(MakeVm());
        try
        {
            Assert.Equal(80, host.Height); // ToastWindow Height="80" (DIP)
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void CloseCommand_RunsAndWindowCloses()
    {
        bool closed = false;
        var vm = MakeVm(close: () => closed = true);
        var w = new ToastWindow(vm);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();
            vm.CloseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(closed);
        }
        finally
        {
            w.Close();
        }
    }

    [AvaloniaFact]
    public void ActivateCommand_Runs()
    {
        bool activated = false;
        var vm = MakeVm(activate: () => activated = true);
        var w = new ToastWindow(vm);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();
            vm.ActivateCommand.Execute(null);
            Assert.True(activated);
        }
        finally
        {
            w.Close();
        }
    }

    [AvaloniaFact]
    public void AutoDismiss_ArmsOnOpen_PausesOnHover_ResumesOnLeave()
    {
        var w = new ToastWindow(MakeVm());
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(w.IsAutoDismissRunning); // Opened armed the 5s auto-dismiss

            w.PauseAutoDismiss();                // pointer-enter path
            Assert.False(w.IsAutoDismissRunning);

            w.RestartAutoDismiss();              // pointer-leave path
            Assert.True(w.IsAutoDismissRunning);
        }
        finally
        {
            w.Close();
        }
    }

    [AvaloniaFact]
    public void AutoDismiss_StopsOnClose()
    {
        var w = new ToastWindow(MakeVm());
        w.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.IsAutoDismissRunning);

        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.IsAutoDismissRunning); // Closed stopped the timer
    }

    [AvaloniaFact]
    public void Host_TopLeftDip_WriteThroughToWindowPosition_ViaPlacement()
    {
        var window = new ToastWindow(MakeVm());
        var host = new AvaloniaToastHost(window);
        try
        {
            // The service writes DIP Top/Left (Reflow); the host must push them onto Window.Position
            // (physical px) through ToastPlacement.DipToPhysical. Headless has no desktop lifetime, so
            // ResolvePrimaryScaling falls back to 1.0 → DIP == physical, isolating the delegation.
            host.Left = 200;
            host.Top = 100;

            Assert.Equal(new PixelPoint(200, 100), window.Position);
        }
        finally
        {
            window.Close();
        }
    }
}
