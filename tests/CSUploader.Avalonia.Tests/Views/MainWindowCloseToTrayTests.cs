// <copyright file="MainWindowCloseToTrayTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CSUploader.Dal;
using CSUploader.Services;
using CSUploader.Upload;
using CSUploader.Views;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless close/minimize-to-tray tests for <see cref="MainWindow"/> (Phase 7 Task 6, port of the WPF
/// MainWindow, rules 43 + 44). Covers the full Closing matrix: <c>CloseAction.Exit</c> closes;
/// <c>MinimizeToTray</c> reroutes to the tray (hide + <c>UpdateVisibility</c> + the ONE first-hide
/// <c>NotifyHidden</c> balloon); a WindowState minimize hides WITHOUT a balloon; the <c>Ask</c> outcomes
/// (Exit+Remember persists, Minimize hides without a balloon and does not persist, Cancel keeps the window
/// open) via the <see cref="MainWindow.ApplyCloseActionChoiceAsync"/> seam — Avalonia.Headless cannot click
/// a modal <c>ShowDialog</c>; and <c>_forceClose</c> (menu Exit) bypassing the reroute. Every shown window
/// is closed in a <c>finally</c> (headless windows are process-global for the session).
/// </summary>
public class MainWindowCloseToTrayTests
{
    [AvaloniaFact]
    public void Close_WithMinimizeToTray_ReroutesToTray_NotClosed()
    {
        var settings = new AppSettings { CloseAction = CloseAction.MinimizeToTray };
        var tray = new Mock<ITrayIconService>();
        (SettingRepository repo, SqliteConnection conn) = StubRepo();
        var w = new MainWindow(settings, tray.Object, repo);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();

            w.Close(); // triggers Closing -> MinimizeToTray reroute (e.Cancel = true)
            Dispatcher.UIThread.RunJobs();

            Assert.False(w.IsVisible);            // hidden, not closed
            tray.Verify(t => t.UpdateVisibility(), Times.AtLeastOnce);
            tray.Verify(t => t.NotifyHidden(), Times.Once); // the direct MinimizeToTray Closing branch balloons
        }
        finally
        {
            settings.CloseAction = CloseAction.Exit; // let the finally Close() actually close
            w.Close();
            conn.Dispose();
        }
    }

    [AvaloniaFact]
    public void Close_WithExit_ActuallyCloses()
    {
        var settings = new AppSettings { CloseAction = CloseAction.Exit };
        (SettingRepository repo, SqliteConnection conn) = StubRepo();
        var w = new MainWindow(settings, Mock.Of<ITrayIconService>(), repo);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();
            w.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.False(w.IsVisible);
        }
        finally
        {
            w.Close();
            conn.Dispose();
        }
    }

    [AvaloniaFact]
    public void Minimize_WithMinimizeToTray_Hides()
    {
        var settings = new AppSettings { MinimizeToTray = true, CloseAction = CloseAction.Exit };
        var tray = new Mock<ITrayIconService>();
        (SettingRepository repo, SqliteConnection conn) = StubRepo();
        var w = new MainWindow(settings, tray.Object, repo);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();

            w.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();

            Assert.False(w.IsVisible);
            tray.Verify(t => t.UpdateVisibility(), Times.AtLeastOnce);
            tray.Verify(t => t.NotifyHidden(), Times.Never); // WindowState minimize hides with NO balloon
        }
        finally
        {
            w.Close();
            conn.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AskExit_WithRemember_PersistsCloseAction_AndCloses()
    {
        // Ask -> the user picks Exit and ticks Remember: persist to AppSettings AND the SettingRepository,
        // then _forceClose lets the re-close bypass the would-be reroute. CloseAction starts at
        // MinimizeToTray so the close proves the Exit choice bypasses that reroute. Driven through the
        // ApplyCloseActionChoiceAsync seam (the modal CloseActionDialog cannot be clicked headlessly).
        var settings = new AppSettings { CloseAction = CloseAction.MinimizeToTray };
        var tray = new Mock<ITrayIconService>();
        (SettingRepository repo, SqliteConnection conn) = StubRepo();
        var w = new MainWindow(settings, tray.Object, repo);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();

            await w.ApplyCloseActionChoiceAsync(new CloseActionChoice(CloseAction.Exit, Remember: true));
            Dispatcher.UIThread.RunJobs();

            Assert.False(w.IsVisible);                             // Exit closed the window
            Assert.Equal(CloseAction.Exit, settings.CloseAction);  // in-memory updated
            SettingDto? persisted = await repo.FindByKeyAsync(SettingKey.CloseAction);
            Assert.Equal("Exit", persisted?.Value);                // persisted to the DB
            tray.Verify(t => t.NotifyHidden(), Times.Never);       // Exit never balloons
        }
        finally
        {
            w.Close();
            conn.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AskMinimize_NoRemember_HidesWithoutBalloon_AndDoesNotPersist()
    {
        // Ask -> the user picks MinimizeToTray WITHOUT Remember: hide + refresh the tray, but NO first-hide
        // balloon (the pinned Ask->Minimize discipline: NotifyHidden fires ONLY on the direct MinimizeToTray
        // Closing branch, never here) and nothing persisted (Remember = false leaves AppSettings at Ask).
        var settings = new AppSettings { CloseAction = CloseAction.Ask };
        var tray = new Mock<ITrayIconService>();
        (SettingRepository repo, SqliteConnection conn) = StubRepo();
        var w = new MainWindow(settings, tray.Object, repo);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();

            await w.ApplyCloseActionChoiceAsync(new CloseActionChoice(CloseAction.MinimizeToTray, Remember: false));
            Dispatcher.UIThread.RunJobs();

            Assert.False(w.IsVisible);                             // hidden
            tray.Verify(t => t.UpdateVisibility(), Times.Once);    // refreshed
            tray.Verify(t => t.NotifyHidden(), Times.Never);       // NO balloon on the Ask->Minimize branch
            Assert.Equal(CloseAction.Ask, settings.CloseAction);   // unchanged (Remember = false)
            Assert.Null(await repo.FindByKeyAsync(SettingKey.CloseAction)); // not persisted
        }
        finally
        {
            settings.CloseAction = CloseAction.Exit; // window was hidden, not closed — let finally close it
            w.Close();
            conn.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AskCancelled_KeepsWindowOpen_AndDoesNotPersist()
    {
        // Ask -> the user cancels the prompt (null choice): the window stays open, AppSettings is untouched,
        // nothing is persisted, and the tray is never touched.
        var settings = new AppSettings { CloseAction = CloseAction.Ask };
        var tray = new Mock<ITrayIconService>();
        (SettingRepository repo, SqliteConnection conn) = StubRepo();
        var w = new MainWindow(settings, tray.Object, repo);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();

            await w.ApplyCloseActionChoiceAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(w.IsVisible);                              // still open
            Assert.Equal(CloseAction.Ask, settings.CloseAction);  // unchanged
            Assert.Null(await repo.FindByKeyAsync(SettingKey.CloseAction));
            tray.Verify(t => t.UpdateVisibility(), Times.Never);
            tray.Verify(t => t.NotifyHidden(), Times.Never);
        }
        finally
        {
            settings.CloseAction = CloseAction.Exit;
            w.Close();
            conn.Dispose();
        }
    }

    [AvaloniaFact]
    public void ForceClose_ViaMenuExit_BypassesMinimizeToTrayReroute()
    {
        // File -> Exit sets _forceClose and calls Close(); even with CloseAction = MinimizeToTray (which
        // would otherwise reroute to the tray), the guard makes the re-entrant Closing return -> the window
        // really closes and the tray reroute (UpdateVisibility / NotifyHidden) never runs.
        var settings = new AppSettings { CloseAction = CloseAction.MinimizeToTray };
        var tray = new Mock<ITrayIconService>();
        (SettingRepository repo, SqliteConnection conn) = StubRepo();
        var w = new MainWindow(settings, tray.Object, repo);
        try
        {
            w.Show();
            Dispatcher.UIThread.RunJobs();

            Menu menu = w.GetVisualDescendants().OfType<Menu>().First();
            var file = (MenuItem)menu.Items[0]!;
            var exit = (MenuItem)file.Items[0]!;
            exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.False(w.IsVisible);                             // closed outright, not rerouted
            tray.Verify(t => t.UpdateVisibility(), Times.Never);   // reroute bypassed
            tray.Verify(t => t.NotifyHidden(), Times.Never);
        }
        finally
        {
            w.Close();
            conn.Dispose();
        }
    }

    private static (SettingRepository, SqliteConnection) StubRepo()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
            .UseSqlite(conn).Options;
        var factory = new TestDbContextFactory(options);
        using (CSUploaderDbContext db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }

        return (new SettingRepository(factory), conn);
    }

    private sealed class TestDbContextFactory(DbContextOptions<CSUploaderDbContext> options)
        : IDbContextFactory<CSUploaderDbContext>
    {
        public CSUploaderDbContext CreateDbContext() => new(options);
    }
}
