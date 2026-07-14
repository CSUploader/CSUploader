// <copyright file="AvaloniaToastWiringTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Reflection;
using Avalonia.Headless.XUnit;
using CSUploader.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CSUploader.Tests.Avalonia.Services;

/// <summary>
/// Phase 7 Task 3 DI-wiring tests: the head composes the REAL <see cref="ToastNotificationService"/>
/// (not the Phase 2 <c>NoOpToastNotificationService</c>) plus <see cref="AvaloniaToastWindowFactory"/>,
/// so <c>UploadNotificationListener</c> raises live completion toasts. Both tests build the same graph
/// the shipping head composes (<c>App.ConfigureServices</c>) and open NO window — the ownerless toast
/// window a live fire would create is uncloseable under the headless lifetime (no
/// <c>IClassicDesktopStyleApplicationLifetime.Windows</c>), so the end-to-end raise is bridge-verified
/// (Step 6) rather than asserted here; the Core <c>ToastNotificationService</c> stacking/positioning
/// behaviour is already covered by <c>tests/Services/ToastNotificationServiceTests.cs</c>.
/// </summary>
public class AvaloniaToastWiringTests
{
    [Fact]
    public void ToastService_ResolvesToRealService_NotNoOp()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "csu-toast-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var services = new ServiceCollection();
            App.ConfigureServices(services, baseDir);
            using ServiceProvider sp = services.BuildServiceProvider();

            Assert.IsType<ToastNotificationService>(sp.GetRequiredService<IToastNotificationService>());
            Assert.IsType<AvaloniaToastWindowFactory>(sp.GetRequiredService<IToastWindowFactory>());
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Exercises the wired <c>workAreaProvider</c> closure the DI factory composes into the real service.
    /// The closure is private to the DI registration and is not observable through any public seam under
    /// the headless lifetime (it would only surface via a toast window's Position, and the ownerless toast
    /// window cannot be captured/closed here); it is pulled off the resolved service by its unique
    /// <see cref="Func{DipRect}"/> field type and invoked. With no
    /// <c>IClassicDesktopStyleApplicationLifetime.MainWindow</c> under the headless session the closure
    /// takes its documented fallback (the whole 1920x1080 primary area in DIPs) — proving it invokes
    /// cleanly and returns a non-degenerate rect (a NoOp registration has no such closure, so this is red
    /// until Task 3's swap).
    /// </summary>
    [AvaloniaFact]
    public void WorkAreaProvider_InvokesCleanly_ReturnsNonDegenerateDipRect()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "csu-toast-workarea-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var services = new ServiceCollection();
            App.ConfigureServices(services, baseDir);
            using ServiceProvider sp = services.BuildServiceProvider();

            var service = Assert.IsType<ToastNotificationService>(sp.GetRequiredService<IToastNotificationService>());

            FieldInfo field = typeof(ToastNotificationService)
                .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(f => f.FieldType == typeof(Func<DipRect>));
            var workAreaProvider = (Func<DipRect>)field.GetValue(service)!;

            DipRect work = workAreaProvider(); // headless: no desktop MainWindow -> documented fallback

            Assert.Equal(0, work.X);
            Assert.Equal(0, work.Y);
            Assert.Equal(1920, work.Width);
            Assert.Equal(1080, work.Height);
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
