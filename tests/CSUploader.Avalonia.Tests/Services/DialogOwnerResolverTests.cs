// <copyright file="DialogOwnerResolverTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CSUploader.Services;

namespace CSUploader.Tests.Avalonia.Services;

/// <summary>
/// Headless policy tests for <see cref="DialogOwnerResolver"/> (Phase 4 prep item 2): the owner chain
/// is active-visible window ?? visible main window ?? null. Real windows via the Phase 3 headless App.
/// <para>
/// Reality-check #6 finding, recorded here so later readers don't retry the plan's original phrasing:
/// the headless session reports <see cref="Window.IsActive"/> == <c>true</c> for EVERY shown window and
/// never deactivates a previous one, so <c>Activate()</c> cannot make "the second window" exclusively
/// win; and both <c>Hide()</c> and a direct <c>IsVisible = false</c> clear <c>IsActive</c> too. So the
/// two cases that need a shown-but-inactive or active-but-invisible window are driven through the pure
/// <see cref="DialogOwnerResolver.Resolve"/> core — <see cref="NoActiveVisibleWindow_FallsBackToVisibleMainWindow"/>
/// with an empty window list, and <see cref="HiddenActiveIsSkipped"/> by force-setting the private
/// <c>IsActive</c> backing field to construct the exact adversarial state its <c>IsVisible</c> conjunct
/// defends against.
/// </para>
/// Every shown window is closed in a <c>finally</c> (headless windows are process-global for the session).
/// </summary>
public class DialogOwnerResolverTests
{
    [AvaloniaFact]
    public void ActiveVisibleWindow_Wins()
    {
        var dialog = new Window { Width = 100, Height = 100 };
        var main = new Window { Width = 100, Height = 100 };
        try
        {
            dialog.Show();
            main.Show();
            Dispatcher.UIThread.RunJobs();

            // Precondition: both are active + visible under headless (see the class-level note).
            Assert.True(dialog is { IsActive: true, IsVisible: true });
            Assert.True(main is { IsActive: true, IsVisible: true });

            // Branch 1 beats branch 2: an active-visible window in the list is preferred over the
            // main-window fallback.
            Assert.Same(dialog, DialogOwnerResolver.Resolve(new[] { dialog }, main));

            // FirstOrDefault ordering: the first active-visible window wins.
            Assert.Same(dialog, DialogOwnerResolver.Resolve(new[] { dialog, main }, mainWindow: null));
        }
        finally
        {
            dialog.Close();
            main.Close();
        }
    }

    [AvaloniaFact]
    public void NoActiveVisibleWindow_FallsBackToVisibleMainWindow()
    {
        var main = new Window { Width = 100, Height = 100 };
        try
        {
            main.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(main.IsVisible);

            // Headless cannot produce a shown-but-inactive window, so the "nothing active" branch is
            // exercised through the pure core with an empty window list: no active-visible candidate →
            // the visible main window is the fallback.
            Assert.Same(main, DialogOwnerResolver.Resolve(Array.Empty<Window>(), main));
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact]
    public void TrayHiddenMainWindow_YieldsNull()
    {
        var main = new Window { Width = 100, Height = 100 };
        try
        {
            main.Show();
            Dispatcher.UIThread.RunJobs();
            main.Hide();
            Dispatcher.UIThread.RunJobs();
            Assert.False(main.IsVisible);

            // The load-bearing case: main hidden to the tray, no other windows → null. This is what
            // forces the caller-side reveal/ownerless split (the WPF head never had to decide this).
            Assert.Null(DialogOwnerResolver.Resolve(new[] { main }, main));
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact]
    public void HiddenActiveIsSkipped()
    {
        // The IsVisible conjunct guards against a window that is active but not visible. Headless couples
        // visibility and activation (both Hide() and IsVisible=false clear IsActive), so that adversarial
        // state is constructed by force-setting the private IsActive backing field. Without the conjunct,
        // this ghost would wrongly win.
        var ghost = new Window { Width = 100, Height = 100, IsVisible = false };
        ForceIsActive(ghost);
        Assert.True(ghost is { IsActive: true, IsVisible: false }); // precondition

        Assert.Null(DialogOwnerResolver.Resolve(new[] { ghost }, mainWindow: null));
    }

    [AvaloniaFact]
    public void ResolveFromLifetime_UnderNonDesktopLifetime_ReturnsNull()
    {
        // The headless session is not an IClassicDesktopStyleApplicationLifetime, so the lifetime-reading
        // wrapper short-circuits to null — the documented reason the pure Resolve core is factored out as
        // the only headlessly testable half. Pins the wrapper's non-desktop contract.
        Assert.Null(DialogOwnerResolver.ResolveFromLifetime());
    }

    // Force WindowBase._isActive (a read-only DirectProperty with a private setter) true, so the CLR
    // IsActive getter — which reads the field directly — reports an active window that is not visible.
    private static void ForceIsActive(Window window)
    {
        FieldInfo field = typeof(WindowBase).GetField("_isActive", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WindowBase._isActive not found — Avalonia internals changed.");
        field.SetValue(window, true);
    }
}
