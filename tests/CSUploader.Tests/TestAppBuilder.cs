// <copyright file="TestAppBuilder.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(CSUploader.Tests.Avalonia.TestAppBuilder))]

// The headless Avalonia session is a single per-assembly UI thread, and several tests mutate
// process-global state on it: the culture-switching LocExtension tests write Localizer.Instance.Culture
// and the theme tests write Application.RequestedThemeVariant / app.Resources. xUnit parallelizes
// distinct test classes by default, so those mutations race — most visibly, a plain [Fact] converter
// test (FileStateDisplay / StartMenuLabel) reads Localizer under a culture another class is flipping.
// Disabling assembly-wide parallelization serializes every test onto the one session; the whole suite
// still runs in ~2s, so the blanket serialize costs nothing and removes the race for good.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CSUploader.Tests.Avalonia;

/// <summary>
/// Headless Avalonia session for the test assembly. Boots the REAL <see cref="global::CSUploader.App"/>
/// (Task 3 swap) so its XAML resource surface — FluentTheme, the DataGrid Fluent styles, and the
/// geometry + bitmap resource dictionaries merged in <c>App.Initialize</c> — is present under test,
/// identical to what the shipping head composes. <c>App.OnFrameworkInitializationCompleted</c>'s DI
/// startup still never runs: the headless session is not an
/// <see cref="global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime"/>,
/// so its desktop-lifetime guard short-circuits (the DI smoke composes <c>App.ConfigureServices</c>
/// directly instead).
/// </summary>
/// <remarks>
/// <see cref="AvaloniaHeadlessPlatformOptions"/> keeps <c>UseSkia</c> OFF: no Phase 3 test asserts
/// rendered pixels, and headless bitmap loading is stubbed — so the <c>new Bitmap(stream)</c> calls in
/// <c>BitmapImageResources.MergeInto</c> return stubs instead of throwing. Flip to
/// <c>UseHeadlessDrawing = false</c> + <c>.UseSkia()</c> only when a test actually asserts rendering.
/// </remarks>
public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<global::CSUploader.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
