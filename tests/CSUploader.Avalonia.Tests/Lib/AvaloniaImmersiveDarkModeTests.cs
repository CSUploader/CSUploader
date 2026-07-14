// <copyright file="AvaloniaImmersiveDarkModeTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CSUploader.Lib.UI;

namespace CSUploader.Tests.Avalonia.Lib;

public class AvaloniaImmersiveDarkModeTests
{
    [Fact]
    public void SetIsDark_UpdatesCache()
    {
        bool original = AvaloniaImmersiveDarkMode.IsDark;
        try
        {
            AvaloniaImmersiveDarkMode.SetIsDark(true);
            Assert.True(AvaloniaImmersiveDarkMode.IsDark);

            AvaloniaImmersiveDarkMode.SetIsDark(false);
            Assert.False(AvaloniaImmersiveDarkMode.IsDark);
        }
        finally
        {
            AvaloniaImmersiveDarkMode.SetIsDark(original);
        }
    }

    [Fact]
    public void RegisterGlobalHandler_IsIdempotent()
    {
        // Registering twice must not throw or double-register (guarded by a static flag).
        AvaloniaImmersiveDarkMode.RegisterGlobalHandler();
        AvaloniaImmersiveDarkMode.RegisterGlobalHandler();
    }

    [AvaloniaFact]
    public void Apply_WithNoPlatformHandle_IsSilentNoOp()
    {
        // NRE-safety (team-lead-requested, plan Step 5): an unshown window has no platform handle, so
        // TryGetPlatformHandle()?.Handle is null and Apply early-returns before any DWM P/Invoke — the
        // exact headless no-op the plan pins. Must not throw for either preference, and must not disturb
        // the cached IsDark (only SetIsDark writes it).
        bool originalCache = AvaloniaImmersiveDarkMode.IsDark;
        Window window = new();
        try
        {
            AvaloniaImmersiveDarkMode.Apply(window, dark: true);
            AvaloniaImmersiveDarkMode.Apply(window, dark: false);
            Assert.Equal(originalCache, AvaloniaImmersiveDarkMode.IsDark);
        }
        finally
        {
            AvaloniaImmersiveDarkMode.SetIsDark(originalCache);
        }
    }
}
