// <copyright file="DeveloperSettingsTabTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CSUploader.Lib.Net.Http;
using CSUploader.Upload;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// The DEBUG-only "Developer" settings category.
/// <para>
/// Only half of this can be observed by running code: the test suite is itself a Debug build, so
/// the release behaviour — the page absent from the compile entirely — is unobservable from here
/// and is pinned by asserting the build switches that produce it. That is weaker than executing
/// it, and deliberately not dressed up as more: what these guard is that somebody deleting the
/// csproj exclusion or the <c>#if DEBUG</c> has to delete a test with it.
/// </para>
/// </summary>
public class DeveloperSettingsTabTests
{
#if DEBUG
    [AvaloniaFact]
    public void DebugBuild_HasAFifthDeveloperCategory_ThatShowsTheDeveloperPage()
    {
        using SettingsViewTests.VmHarness harness = new();
        (Window window, SettingsView view) = SettingsViewTests.Show(harness.Vm);
        try
        {
            Assert.Equal(5, view.Sidebar.ItemCount);

            view.Sidebar.SelectedIndex = 4;
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.Equal(4, harness.Vm.SelectedCategoryIndex);
            DeveloperSettingsView page = Assert.IsType<DeveloperSettingsView>(
                view.GetVisualDescendants().OfType<ContentControl>()
                    .Select(c => c.Content)
                    .OfType<DeveloperSettingsView>()
                    .FirstOrDefault());
            Assert.True(page.IsVisible);

            // The sidebar entry is built in code, so its icon and label are resolved through
            // DynamicResource rather than markup — and an unresolved DynamicResource fails SILENTLY,
            // leaving a blank icon nobody notices. Assert they actually resolved.
            ListBoxItem entry = (ListBoxItem)view.Sidebar.Items[4]!;
            PathIcon icon = ((StackPanel)entry.Content!).Children.OfType<PathIcon>().Single();
            TextBlock label = ((StackPanel)entry.Content!).Children.OfType<TextBlock>().Single();
            Assert.NotNull(icon.Data);
            Assert.NotNull(icon.Foreground);
            Assert.False(string.IsNullOrWhiteSpace(label.Text));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheOtherFourCategories_StillSwitchAsBefore()
    {
        // The Developer entry is appended, so the existing indices must be untouched — a page the
        // user cannot reach in a release build must not renumber the ones they can.
        using SettingsViewTests.VmHarness harness = new();
        (Window window, SettingsView view) = SettingsViewTests.Show(harness.Vm);
        try
        {
            foreach (int index in (int[])[0, 1, 2, 3])
            {
                view.Sidebar.SelectedIndex = index;
                global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Equal(index, harness.Vm.SelectedCategoryIndex);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void MockServer_IsHonouredInDebug()
    {
        AppSettings settings = new() { UseMockServer = true, MockServerBaseUrl = "http://localhost:8080" };

        Assert.True(MockServerConfig.FromAppSettings(settings).Enabled);
    }
#endif

    [Fact]
    public void ReleaseBuilds_ExcludeTheDeveloperPageFromTheCompile()
    {
        // Asserted against the build file because the effect cannot be observed from a Debug-built
        // test. Verified once by hand at the time of writing: the string "DeveloperSettingsView"
        // appears in bin/Debug/CSUploader.dll and does NOT appear in bin/Release/CSUploader.dll.
        string csproj = File.ReadAllText(Path.Combine(
            RepoXaml.FindRepoRoot(), "src", "CSUploader", "CSUploader.csproj"));

        Match group = Regex.Match(
            csproj,
            @"<ItemGroup\s+Condition=""'\$\(Configuration\)'\s*!=\s*'Debug'"">(?<body>.*?)</ItemGroup>",
            RegexOptions.Singleline);

        Assert.True(group.Success, "no non-Debug ItemGroup — the Developer page would ship in releases");
        Assert.Contains(@"<Compile Remove=""Views\DeveloperSettingsView.axaml.cs"" />", group.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains(@"<AvaloniaXaml Remove=""Views\DeveloperSettingsView.axaml"" />", group.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOnlyReferenceToTheDeveloperPage_StaysBehindIfDebug()
    {
        // The exclusion above and this guard hold each other up: excluding the file while something
        // still named the type would fail the release build outright, which is the desired failure
        // mode — loud, at build time — rather than a page that ships by accident.
        string codeBehind = File.ReadAllText(Path.Combine(
            RepoXaml.FindRepoRoot(), "src", "CSUploader", "Views", "SettingsView.axaml.cs"));

        foreach (Match reference in Regex.Matches(codeBehind, @"DeveloperSettingsView|DeveloperHost"))
        {
            string before = codeBehind[..reference.Index];
            int openGuard = before.LastIndexOf("#if DEBUG", StringComparison.Ordinal);
            int closeGuard = before.LastIndexOf("#endif", StringComparison.Ordinal);
            Assert.True(
                openGuard >= 0 && openGuard > closeGuard,
                $"'{reference.Value}' at offset {reference.Index} is not inside an #if DEBUG block — "
                    + "a release build would fail to compile, or worse, ship the page.");
        }
    }

    [Fact]
    public void MockServerFlag_CannotEscapeIntoAReleaseBuild()
    {
        // Debug and release builds on one machine share a settings database, so the persisted flag
        // outlives the DEBUG-only switch that sets it. Same reasoning as above: the release branch
        // is unobservable from here, so the guard itself is what gets pinned.
        string source = File.ReadAllText(Path.Combine(
            RepoXaml.FindRepoRoot(), "src", "CSUploader.Core", "Lib", "Net", "Http", "MockServerConfig.cs"));

        int method = source.IndexOf("FromAppSettings(AppSettings settings)", StringComparison.Ordinal);
        Assert.True(method >= 0, "FromAppSettings is gone — the guard cannot be checked");

        string body = source[method..];
        Assert.Contains("#if DEBUG", body, StringComparison.Ordinal);
        Assert.Contains("return Disabled;", body, StringComparison.Ordinal);
    }
}
