// <copyright file="InventoryKeyParityTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.IO;
using System.Text.RegularExpressions;

namespace CSUploader.Tests.Lib.Localization;

/// <summary>
/// Every translated inventory carries every key the English one does.
/// <para>
/// <c>md-to-resx.py --check</c> gates each file against its OWN regeneration, so a key added to
/// <c>i18n-inventory.md</c> and forgotten in the other five passes every existing gate — and the
/// missing string surfaces as a raw key in the UI for those five languages, at runtime, in a build
/// nobody flagged. This is the gate that compares them to each other.
/// </para>
/// </summary>
public class InventoryKeyParityTests
{
    private static readonly Regex Entry = new(@"^(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=", RegexOptions.Multiline);

    /// <summary>
    /// Keys that were already English-only when this gate was written, all five languages alike.
    /// They are not broken — <c>ResourceManager</c> falls back to the neutral resource, so they
    /// render in English rather than as raw keys — but they are untranslated, and listing them is
    /// what lets the gate catch the NEXT one without first requiring 51 translations.
    /// <para>Shrink this list; never add to it.</para>
    /// </summary>
    private static readonly HashSet<string> UntranslatedBacklog =
    [
        "Common_CopyLinks",
        "Common_CopyLinks_ByFileBBCode",
        "Common_CopyLinks_ByFileMarkdown",
        "Common_CopyLinks_ByFilePlain",
        "Common_CopyLinks_ByHosterBBCode",
        "Common_CopyLinks_ByHosterMarkdown",
        "Common_CopyLinks_ByHosterPlain",
        "Confirm_RemoveCompletedUploads",
        "EditAccount_ApiKeyLabel",
        "EditAccount_OrLabel",
        "EditAccount_SignInButton",
        "EditAccount_SignInLabel",
        "EditAccount_SignIn_FailedGeneric",
        "EditAccount_SignIn_Failed_Format",
        "EditAccount_SignIn_InProgress",
        "EditAccount_SignIn_Success",
        "EditAccount_SignIn_SuccessAs_Format",
        "EditAccount_SignIn_Unavailable",
        "EditAccount_Validation_RequireLoginOrApiKey",
        "Logs_Col_Method",
        "Logs_Col_Proxy",
        "Logs_Col_Url",
        "Logs_ResetColumns_Message",
        "Logs_ResetColumns_Title",
        "Uploaded_Col_Started",
        "Uploads_Col_Started",
        "Uploads_Context_RemoveAllCompleted",
        "Uploads_Context_RenamePackage",
        "Uploads_Overview_Elapsed",
        "Uploads_Overview_ElapsedLabel",
        "Uploads_Overview_FinishAt",
        "Uploads_Overview_FinishAtLabel",
        "Uploads_RemoveCompleted_Format",
        "Uploads_Reset_Multi_Format",
        "WebViewLogin_Error_InitFailed_Format",
        "WebViewLogin_Error_SocksAuthUnsupported_Format",
        "WebViewLogin_Error_UnsupportedProxy_Title",
        "WebViewLogin_Header_Format",
        "WebViewLogin_Instructions",
        "WebViewLogin_Status_CookieReadFailed_Format",
        "WebViewLogin_Status_Initializing",
        "WebViewLogin_Status_Loading_Format",
        "WebViewLogin_WindowTitle",
        "Wizard_Summary_AutoFitNoticeWithFree_Format",
        "Wizard_Summary_AutoFitNotice_Format",
        "Wizard_Summary_CheckingSpace",
        "Wizard_Summary_FilesSelected_Format",
        "Wizard_Summary_OverCapacityHint",
        "Wizard_Summary_SelectedOfFree_Format",
        "Wizard_Summary_ToUpload_Format",
        "Wizard_Summary_TotalFooter_Format",
    ];

    private static string InventoryDirectory()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "docs");
    }

    /// <summary>Keys inside the fenced blocks — the only lines the generator reads.</summary>
    private static HashSet<string> KeysOf(string path)
    {
        string text = File.ReadAllText(path);
        HashSet<string> keys = [];
        bool inFence = false;

        foreach (string line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence && Entry.Match(line) is { Success: true } m)
            {
                keys.Add(m.Groups["key"].Value);
            }
        }

        return keys;
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("vi")]
    [InlineData("fil")]
    public void EveryTranslatedInventory_CarriesEveryEnglishKey(string culture)
    {
        string docs = InventoryDirectory();
        HashSet<string> english = KeysOf(Path.Combine(docs, "i18n-inventory.md"));
        HashSet<string> translated = KeysOf(Path.Combine(docs, $"i18n-inventory.{culture}.md"));

        Assert.NotEmpty(english);

        string[] missing = [.. english.Except(translated).Except(UntranslatedBacklog).Order()];
        Assert.True(
            missing.Length == 0,
            $"i18n-inventory.{culture}.md is missing {missing.Length} key(s) the English inventory has: "
            + string.Join(", ", missing.Take(20))
            + ". Add them there (the English text is fine for an untranslated string) and regenerate "
            + "that culture's resx with scripts/md-to-resx.py.");

        // ...and the backlog must not outlive the debt: a key translated everywhere should leave
        // the list, or the gate quietly stops watching it.
        string[] stale = [.. UntranslatedBacklog.Intersect(translated).Order()];
        Assert.True(
            stale.Length == 0,
            $"{stale.Length} key(s) are listed as untranslated but present in {culture}: "
            + string.Join(", ", stale.Take(20)) + ". Remove them from UntranslatedBacklog.");
    }
}
