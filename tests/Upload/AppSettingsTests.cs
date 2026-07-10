// <copyright file="AppSettingsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Upload;

namespace CSUploader.Tests.Upload;

/// <summary>
/// Unit tests for <see cref="AppSettings"/>'s <see cref="AppSettings.ForceAutostartUploadsNever"/>
/// latch — the mechanism behind the Avalonia head's <c>--agent</c> safety guard.
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void AutostartUploads_AfterForceNever_ReportsNeverEvenWhenSetAgain()
    {
        AppSettings settings = new() { AutostartUploads = AutostartUploadsMode.Always };
        Assert.Equal(AutostartUploadsMode.Always, settings.AutostartUploads);

        settings.ForceAutostartUploadsNever();
        Assert.Equal(AutostartUploadsMode.Never, settings.AutostartUploads);

        // A later write (e.g. SettingsViewModel copying the persisted policy back during load)
        // still records the value, but the latched getter keeps reporting Never.
        settings.AutostartUploads = AutostartUploadsMode.Always;
        Assert.Equal(AutostartUploadsMode.Never, settings.AutostartUploads);
    }

    [Fact]
    public void AutostartUploads_Unlatched_HonoursTheSetValueAndDefault()
    {
        AppSettings settings = new();

        // Unset → the default policy.
        Assert.Equal(AppSettings.DefaultAutostartUploads, settings.AutostartUploads);

        settings.AutostartUploads = AutostartUploadsMode.Never;
        Assert.Equal(AutostartUploadsMode.Never, settings.AutostartUploads);

        settings.AutostartUploads = AutostartUploadsMode.Always;
        Assert.Equal(AutostartUploadsMode.Always, settings.AutostartUploads);
    }
}
