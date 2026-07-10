// <copyright file="TestAppBuilder.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(CSUploader.Tests.Avalonia.TestAppBuilder))]

namespace CSUploader.Tests.Avalonia;

/// <summary>
/// Headless Avalonia session for the test assembly. Uses a bare <see cref="TestApp"/> — the DI smoke
/// composes the real <c>App.ConfigureServices</c> graph directly, so the real head's desktop-lifetime
/// startup never runs under test. <see cref="AvaloniaHeadlessPlatformOptions"/> defaults (no
/// <c>UseSkia</c>) are enough here: these tests resolve services and drive the dispatcher, they render
/// no bitmaps.
/// </summary>
public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class TestApp : Application
{
}
