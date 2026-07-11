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
/// cases that need a shown-but-inactive or active-but-invisible window are driven through the pure
/// <see cref="DialogOwnerResolver.Resolve"/> core with the private <c>IsActive</c> backing field
/// force-set to the state headless input cannot produce:
/// <see cref="HiddenActiveIsSkipped"/> pins the <c>IsVisible</c> conjunct (active but hidden → lose) and
/// <see cref="InactiveVisibleWindow_IsSkipped_VisibleMainWins"/> pins the <c>IsActive</c> conjunct
/// (visible but inactive → skipped, visible-main wins). Together they defend both halves of branch 1
/// against a dropped conjunct.
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
        SetIsActive(ghost, true);
        Assert.True(ghost is { IsActive: true, IsVisible: false }); // precondition

        Assert.Null(DialogOwnerResolver.Resolve(new[] { ghost }, mainWindow: null));
    }

    [AvaloniaFact]
    public void InactiveVisibleWindow_IsSkipped_VisibleMainWins()
    {
        // Symmetric guard to HiddenActiveIsSkipped: that pins the IsVisible conjunct (an active-but-hidden
        // window must lose); this pins the IsActive conjunct (a visible-but-inactive window must be
        // skipped so the visible main window wins). Headless keeps every shown window IsActive==true and
        // never deactivates one, so the inactive state is force-cleared on a genuinely shown (visible)
        // window. Without the IsActive conjunct, FirstOrDefault(w => w.IsVisible) would wrongly return
        // this window instead of falling through to the visible-main branch.
        var shownInactive = new Window { Width = 100, Height = 100 };
        var main = new Window { Width = 100, Height = 100 };
        try
        {
            shownInactive.Show();
            main.Show();
            Dispatcher.UIThread.RunJobs();
            SetIsActive(shownInactive, false);
            Assert.True(shownInactive is { IsActive: false, IsVisible: true }); // precondition
            Assert.True(main.IsVisible);

            // shownInactive is not a branch-1 (active-visible) candidate; branch 2 (visible main) wins.
            Assert.Same(main, DialogOwnerResolver.Resolve(new[] { shownInactive }, main));
        }
        finally
        {
            shownInactive.Close();
            main.Close();
        }
    }

    [AvaloniaFact]
    public void ResolveFromLifetime_UnderNonDesktopLifetime_ReturnsNull()
    {
        // The headless session is not an IClassicDesktopStyleApplicationLifetime, so the lifetime-reading
        // wrapper short-circuits to null — the documented reason the pure Resolve core is factored out as
        // the only headlessly testable half. Pins the wrapper's non-desktop contract.
        Assert.Null(DialogOwnerResolver.ResolveFromLifetime());
    }

    // ── ResolveVisibleMainOnly: the long-lived, non-modal surface owner (visible main window ONLY) ──

    [AvaloniaFact]
    public void ResolveVisibleMainOnly_VisibleMain_ReturnsMain()
    {
        var main = new Window { Width = 100, Height = 100 };
        try
        {
            main.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(main.IsVisible);

            // The update-progress window's owner. There is no window-list parameter: the policy deliberately
            // ignores active windows so a long-lived surface never parents to a transient one it must outlive.
            Assert.Same(main, DialogOwnerResolver.ResolveVisibleMainOnly(main));
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact]
    public void ResolveVisibleMainOnly_HiddenMain_ReturnsNull()
    {
        var main = new Window { Width = 100, Height = 100 };
        try
        {
            main.Show();
            Dispatcher.UIThread.RunJobs();
            main.Hide();
            Dispatcher.UIThread.RunJobs();
            Assert.False(main.IsVisible);

            // Main hidden to the tray → null, so the sink shows the progress window ownerless (Avalonia's
            // Show(owner) rejects a hidden owner).
            Assert.Null(DialogOwnerResolver.ResolveVisibleMainOnly(main));
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact]
    public void ResolveVisibleMainOnly_UnderNonDesktopLifetime_ReturnsNull()
        => Assert.Null(DialogOwnerResolver.ResolveVisibleMainOnly());

    // ── ResolveTopLevelForClipboard: the clipboard TopLevel (active-visible ?? main REGARDLESS of visibility) ──

    [AvaloniaFact]
    public void ResolveTopLevelForClipboard_ActiveVisibleWindow_Wins()
    {
        var active = new Window { Width = 100, Height = 100 };
        var main = new Window { Width = 100, Height = 100 };
        try
        {
            active.Show();
            main.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Same(active, DialogOwnerResolver.ResolveTopLevelForClipboard(new[] { active }, main));
        }
        finally
        {
            active.Close();
            main.Close();
        }
    }

    [AvaloniaFact]
    public void ResolveTopLevelForClipboard_NoActive_ReturnsMain_EvenWhenHidden()
    {
        var main = new Window { Width = 100, Height = 100 };
        try
        {
            main.Show();
            Dispatcher.UIThread.RunJobs();
            main.Hide();
            Dispatcher.UIThread.RunJobs();
            Assert.False(main.IsVisible);

            // The load-bearing divergence from Resolve: a clipboard hangs off a TopLevel's platform impl,
            // which is live on a tray-hidden window, so the hidden main is STILL returned (Resolve would drop
            // it to null). Without this, a Copy issued while the main window is minimized to the tray no-ops.
            Assert.Same(main, DialogOwnerResolver.ResolveTopLevelForClipboard(Array.Empty<Window>(), main));
        }
        finally
        {
            main.Close();
        }
    }

    [AvaloniaFact]
    public void ResolveTopLevelForClipboard_NoWindowAtAll_ReturnsNull()
        => Assert.Null(DialogOwnerResolver.ResolveTopLevelForClipboard(Array.Empty<Window>(), mainWindow: null));

    [AvaloniaFact]
    public void ResolveTopLevelForClipboard_UnderNonDesktopLifetime_ReturnsNull()
        => Assert.Null(DialogOwnerResolver.ResolveTopLevelForClipboard());

    // Force WindowBase._isActive (a read-only DirectProperty with a private setter) to a chosen value,
    // so the CLR IsActive getter — which reads the field directly — reports a state headless input
    // cannot produce: an active window that is not visible, or a visible window that is not active.
    private static void SetIsActive(Window window, bool value)
    {
        FieldInfo field = typeof(WindowBase).GetField("_isActive", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("WindowBase._isActive not found — Avalonia internals changed.");
        field.SetValue(window, value);
    }
}
