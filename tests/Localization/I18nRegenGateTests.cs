// <copyright file="I18nRegenGateTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace CSUploader.Tests.Localization;

/// <summary>
/// Machine-enforces the i18n invariant: every committed Strings*.resx must be byte-identical
/// (modulo line endings) to a regeneration from its docs/i18n-inventory*.md source. A naive
/// regen against a drifted inventory silently DELETES translations, and a hand-edited resx
/// silently drifts from the md — both have bitten before. This gate turns the convention
/// ("edit the md, regenerate, never hand-edit resx") into a failing test, which matters
/// doubly during the Avalonia migration's two-head period where the resx receive merge
/// traffic from two lines of development.
/// </summary>
public class I18nRegenGateTests
{
    public static TheoryData<string, string> Languages => new()
    {
        { "docs/i18n-inventory.md", "src/CSUploader.Core/Resources/Strings.resx" },
        { "docs/i18n-inventory.zh-Hans.md", "src/CSUploader.Core/Resources/Strings.zh-Hans.resx" },
        { "docs/i18n-inventory.ko.md", "src/CSUploader.Core/Resources/Strings.ko.resx" },
        { "docs/i18n-inventory.ja.md", "src/CSUploader.Core/Resources/Strings.ja.resx" },
        { "docs/i18n-inventory.vi.md", "src/CSUploader.Core/Resources/Strings.vi.resx" },
        { "docs/i18n-inventory.fil.md", "src/CSUploader.Core/Resources/Strings.fil.resx" },
    };

    [Theory]
    [MemberData(nameof(Languages))]
    public void Resx_MatchesRegenerationFromInventoryMd(string md, string resx)
    {
        string root = FindRepoRoot();
        ProcessStartInfo psi = new(
            "python",
            $"\"{Path.Combine(root, "scripts", "md-to-resx.py")}\" --check \"{Path.Combine(root, md)}\" \"{Path.Combine(root, resx)}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using Process? proc = TryStart(psi);
        if (proc is null)
        {
            // Locally a missing python just skips the gate; on CI it MUST run, so fail loudly
            // rather than let a silent skip mask i18n drift.
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                Assert.Fail("python is required for the i18n gate on CI");
            }

            return; // python not on PATH locally — the gate still runs on any machine that has it
        }

        // Drain BOTH redirected streams before waiting — an undrained pipe past ~64KB
        // deadlocks the child (stdout is one OK line today, but cheap insurance).
        string stderr = proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd();
        Assert.True(proc.WaitForExit(30_000), "md-to-resx.py --check timed out");
        Assert.True(proc.ExitCode == 0, $"i18n drift for {resx}: {stderr}");
    }

    private static Process? TryStart(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // CallerFilePath, NOT AppContext.BaseDirectory: the repo builds to a temp OutDir
    // (D:\temp2\...) to dodge bin locks, so the binary's directory is outside the repo.
    // Same pattern + rationale as FileHosterIconTests.RepoRoot.
    private static string FindRepoRoot([CallerFilePath] string thisFilePath = "")
    {
        DirectoryInfo? dir = Directory.GetParent(thisFilePath);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CSUploader.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found from " + thisFilePath);
    }
}
