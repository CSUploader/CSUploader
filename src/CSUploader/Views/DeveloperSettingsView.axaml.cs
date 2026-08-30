// <copyright file="DeveloperSettingsView.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;

namespace CSUploader.Views;

/// <summary>
/// The Settings sidebar's "Developer" category: options that exist for local development, which
/// today is the mock-server switch.
/// <para>
/// <b>DEBUG-ONLY.</b> This type and its XAML are dropped from the compile in every non-Debug
/// configuration by a <c>Configuration</c>-conditional <c>ItemGroup</c> in
/// <c>CSUploader.csproj</c>, so a shipped build contains none of it. Its sidebar entry is created
/// in <see cref="SettingsView"/>'s code-behind under <c>#if DEBUG</c>, which is what keeps the two
/// halves from disagreeing: with the file excluded, the code that would reference it is not
/// compiled either.
/// </para>
/// <para>
/// The switch it hosts is only half the story. <c>MockServerConfig.FromAppSettings</c> refuses to
/// honour the persisted flag outside a Debug build, so a value left on here cannot follow the same
/// machine's settings database into a release build and silently redirect real uploads.
/// </para>
/// </summary>
public partial class DeveloperSettingsView : UserControl
{
    public DeveloperSettingsView() => InitializeComponent();
}
