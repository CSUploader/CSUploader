// <copyright file="EditAccountWindowTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CSUploader.Dal;
using CSUploader.Lib.Localization;
using CSUploader.Upload;
using CSUploader.Views;
using static CSUploader.Tests.Avalonia.HeadlessInput;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless behavior tests for <see cref="EditAccountWindow"/> (Phase 5 Task 9), the most stateful port of
/// the phase. The load-bearing checks: the CARRY-FIELD matrix (an edit-Save persists the DTO verbatim, so
/// dropping any carried field — username via the derived-username seed, storage, session cookie + expiry +
/// pin, created stamp — silently blanks account data on the next edit); the three credential-mode row
/// toggles (classic U/P, API-key sign-in, session-cookie sign-in) and their status reset on switch; the
/// three interactive sign-in outcomes (success populates the key + status; failure shows the capped error
/// panel and the Details link opens the full text; a throw surfaces the exception message); the Save
/// validation guards; the nullable-ctor Sign-in-disabled state; the Cancel → null contract; and the prep-
/// item-9 masking (Password AND ApiKey boxes). A real sign-in (WebView) can't run headlessly, so the tests
/// drive a <see cref="FakeInteractiveLogin"/> callback; the Details path is reached through the window's
/// internal <c>ShowErrorDetails</c> seam (a real pointer release on the link isn't drivable headlessly —
/// the Phase 4 §8 sanctioned fallback). Every shown window is closed in a <c>finally</c>.
/// </summary>
public class EditAccountWindowTests
{
    private static readonly string[] Hosters = ["Rapidgator", "KatFile", "Isracloud"];

    // ── Carry-field matrix (prep item 10 / the FileHosterLogin field checklist) ─────────────────────────

    [AvaloniaFact]
    public async Task Save_ApiKeyHoster_CarriesEveryFieldVerbatim_NoReSignIn()
    {
        DateTime created = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        DateTime cookieExpiry = new(2030, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        var seed = new FileHosterLoginDto
        {
            Id = 42,
            FileHosterName = "KatFile",
            Username = "orig_kat_user", // seeds _derivedUsername (there's no username row in api-key mode)
            Password = "ignored-in-api-mode",
            ApiKey = "existing-key",
            AccountType = AccountType.Premium,
            StorageUsedBytes = 123L,
            StorageQuotaBytes = 456L,
            SessionCookie = "sess=abc",
            SessionCookieExpiresUtc = cookieExpiry,
            PinnedProxyId = 7,
            CreatedDateTime = created,
        };

        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditAccountWindow(seed, Hosters);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            Task<FileHosterLoginDto?> dialog = dlg.ShowDialog<FileHosterLoginDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            Click(dlg.SaveButton); // no sign-in; the seeded ApiKey passes validation
            Dispatcher.UIThread.RunJobs();

            FileHosterLoginDto? result = await dialog;
            Assert.NotNull(result);
            Assert.Equal(42, result!.Id);
            Assert.Equal("KatFile", result.FileHosterName);
            Assert.Equal("orig_kat_user", result.Username); // carried via _derivedUsername
            Assert.Equal("existing-key", result.ApiKey);
            Assert.Equal(AccountType.Premium, result.AccountType);
            Assert.Equal(123L, result.StorageUsedBytes);
            Assert.Equal(456L, result.StorageQuotaBytes);
            Assert.Equal("sess=abc", result.SessionCookie);
            Assert.Equal(cookieExpiry, result.SessionCookieExpiresUtc);
            Assert.Equal(7, result.PinnedProxyId);
            Assert.Equal(created, result.CreatedDateTime);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Save_ClassicHoster_EditsUsernamePassword_CarriesSessionStorageCreated()
    {
        DateTime created = new(2024, 12, 31, 23, 59, 58, DateTimeKind.Utc);
        DateTime cookieExpiry = new(2029, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var seed = new FileHosterLoginDto
        {
            Id = 99,
            FileHosterName = "Rapidgator",
            Username = "old_user",
            Password = "old_pass",
            AccountType = AccountType.Premium,
            StorageUsedBytes = 1000L,
            StorageQuotaBytes = 5000L,
            SessionCookie = "sess=xyz",
            SessionCookieExpiresUtc = cookieExpiry,
            PinnedProxyId = 3,
            CreatedDateTime = created,
        };

        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditAccountWindow(seed, Hosters);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            Task<FileHosterLoginDto?> dialog = dlg.ShowDialog<FileHosterLoginDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            dlg.UsernameBox.Text = "new_user";
            dlg.PasswordBox.Text = "new_pass";
            Click(dlg.SaveButton);
            Dispatcher.UIThread.RunJobs();

            FileHosterLoginDto? result = await dialog;
            Assert.NotNull(result);
            Assert.Equal(99, result!.Id);
            Assert.Equal("Rapidgator", result.FileHosterName);
            Assert.Equal("new_user", result.Username); // edited
            Assert.Equal("new_pass", result.Password);  // edited
            Assert.Null(result.ApiKey);                  // classic branch clears it
            Assert.Equal(AccountType.Premium, result.AccountType);
            // Carried across the edit (no re-verify) — dropping any would blank the grid until next refresh.
            Assert.Equal(1000L, result.StorageUsedBytes);
            Assert.Equal(5000L, result.StorageQuotaBytes);
            Assert.Equal("sess=xyz", result.SessionCookie);
            Assert.Equal(cookieExpiry, result.SessionCookieExpiresUtc);
            Assert.Equal(3, result.PinnedProxyId);
            Assert.Equal(created, result.CreatedDateTime);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    // ── Credential-mode row toggles (×3 modes + reset) ──────────────────────────────────────────────────

    [AvaloniaFact]
    public void Mode_ClassicHoster_ShowsUsernamePassword_HidesSignIn()
    {
        var dlg = new EditAccountWindow(new FileHosterLoginDto { FileHosterName = "Rapidgator", AccountType = AccountType.Free }, Hosters);
        try
        {
            Assert.True(dlg.UsernameBox.IsVisible);
            Assert.True(dlg.PasswordBox.IsVisible);
            Assert.False(dlg.SignInRow.IsVisible);
            Assert.False(dlg.OrSeparator.IsVisible);
            Assert.False(dlg.ApiKeyBox.IsVisible);
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void Mode_ApiKeyHoster_ShowsSignInAndApiKey_HidesUsernamePassword()
    {
        var dlg = new EditAccountWindow(new FileHosterLoginDto { FileHosterName = "KatFile", AccountType = AccountType.Free }, Hosters);
        try
        {
            Assert.False(dlg.UsernameBox.IsVisible);
            Assert.False(dlg.PasswordBox.IsVisible);
            Assert.True(dlg.SignInRow.IsVisible);
            Assert.True(dlg.OrSeparator.IsVisible); // "or paste a key" — api-key hosters only
            Assert.True(dlg.ApiKeyBox.IsVisible);
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void Mode_SessionCookieHoster_ShowsSignInOnly_HidesApiKey()
    {
        var dlg = new EditAccountWindow(new FileHosterLoginDto { FileHosterName = "Isracloud", AccountType = AccountType.Free }, Hosters);
        try
        {
            Assert.False(dlg.UsernameBox.IsVisible);
            Assert.True(dlg.SignInRow.IsVisible);
            Assert.False(dlg.OrSeparator.IsVisible); // no pasteable key for session-cookie hosters
            Assert.False(dlg.ApiKeyBox.IsVisible);
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void ModeSwitch_ResetsSignInStatus()
    {
        FakeInteractiveLogin login = FakeInteractiveLogin.Success(
            new AccountCheckResult(true, AccountType.Premium, ApiKey: "k", DerivedUsername: "kat@example.test"));

        // Add mode (Id 0) so the combo is live and starts on an api-key hoster.
        var dlg = new EditAccountWindow(
            new FileHosterLoginDto { FileHosterName = "KatFile", AccountType = AccountType.Free }, Hosters, login.Callback);
        try
        {
            Click(dlg.SignInButton);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("success", dlg.SignInStatus.Classes); // signed-in look showing

            dlg.HosterCombo.SelectedItem = "Rapidgator"; // switch hoster → RefreshCredentialMode resets status
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain("success", dlg.SignInStatus.Classes);
            Assert.False(dlg.SignInErrorPanel.IsVisible);
        }
        finally
        {
            dlg.Close();
        }
    }

    // ── Interactive sign-in outcomes (×3) ───────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task SignIn_Success_PopulatesApiKeyAndStatus_SaveCarriesDerived()
    {
        FakeInteractiveLogin login = FakeInteractiveLogin.Success(new AccountCheckResult(
            true, AccountType.Premium,
            ApiKey: "derived-key",
            DerivedUsername: "kat@example.test",
            StorageUsedBytes: 100L,
            StorageQuotaBytes: 200L));

        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditAccountWindow(
            new FileHosterLoginDto { FileHosterName = "KatFile", AccountType = AccountType.Free }, Hosters, login.Callback);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            Task<FileHosterLoginDto?> dialog = dlg.ShowDialog<FileHosterLoginDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            Click(dlg.SignInButton);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, login.CallCount);
            Assert.Equal("KatFile", login.LastHoster);
            Assert.Equal("derived-key", dlg.ApiKeyBox.Text);
            Assert.True(dlg.SignInStatus.IsVisible);
            Assert.Contains("success", dlg.SignInStatus.Classes);
            Assert.False(dlg.SignInErrorPanel.IsVisible);

            Click(dlg.SaveButton);
            Dispatcher.UIThread.RunJobs();

            FileHosterLoginDto? result = await dialog;
            Assert.NotNull(result);
            Assert.Equal("kat@example.test", result!.Username); // the derived username
            Assert.Equal("derived-key", result.ApiKey);
            Assert.Equal(100L, result.StorageUsedBytes);
            Assert.Equal(200L, result.StorageQuotaBytes);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void SignIn_Failure_ShowsErrorPanel_DetailsOpensFullText()
    {
        const string detail = "Sign-in failed: invalid credentials\n<html><body>403 Forbidden — full raw body</body></html>";
        FakeInteractiveLogin login = FakeInteractiveLogin.Failure("invalid credentials", detail);

        var dlg = new EditAccountWindow(
            new FileHosterLoginDto { FileHosterName = "KatFile", AccountType = AccountType.Free }, Hosters, login.Callback);
        ErrorDetailsWindow? details = null;
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            Click(dlg.SignInButton);
            Dispatcher.UIThread.RunJobs();

            Assert.True(dlg.SignInErrorPanel.IsVisible);
            Assert.False(dlg.SignInStatus.IsVisible);
            Assert.Contains("invalid credentials", dlg.SignInErrorText.Text ?? string.Empty, StringComparison.Ordinal);

            // The Details link opens the FULL detail (not the capped preview). Reached through the internal
            // seam — a real pointer release on a TextBlock link isn't drivable headlessly.
            details = dlg.ShowErrorDetails();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(details);
            Assert.Equal(detail, details!.DetailBox.Text);
        }
        finally
        {
            details?.Close();
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void SignIn_Throws_ShowsErrorPanelWithExceptionMessage()
    {
        FakeInteractiveLogin login = FakeInteractiveLogin.Throws(new InvalidOperationException("Synthesized WebView failure"));

        var dlg = new EditAccountWindow(
            new FileHosterLoginDto { FileHosterName = "KatFile", AccountType = AccountType.Free }, Hosters, login.Callback);
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            Click(dlg.SignInButton);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, login.CallCount); // the callback ran (and threw)
            Assert.True(dlg.SignInErrorPanel.IsVisible);
            Assert.Contains("Synthesized WebView failure", dlg.SignInErrorText.Text ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            dlg.Close();
        }
    }

    // ── Save validation ─────────────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Save_ApiKeyHoster_NoKeyNoCookie_KeepsOpen_ShowsMessageBox()
    {
        var dlg = new EditAccountWindow(
            new FileHosterLoginDto { FileHosterName = "KatFile", AccountType = AccountType.Free }, Hosters);
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            Click(dlg.SaveButton); // empty ApiKey + no captured cookie
            Dispatcher.UIThread.RunJobs();

            Assert.True(dlg.IsVisible);
            Assert.NotNull(dlg.OwnedWindows.OfType<MessageBoxWindow>().FirstOrDefault());
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void Save_SessionCookieHoster_NoCookie_KeepsOpen_ShowsMessageBox()
    {
        var dlg = new EditAccountWindow(
            new FileHosterLoginDto { FileHosterName = "Isracloud", AccountType = AccountType.Free }, Hosters);
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            Click(dlg.SaveButton); // no ApiKey box shown, no captured cookie
            Dispatcher.UIThread.RunJobs();

            Assert.True(dlg.IsVisible);
            Assert.NotNull(dlg.OwnedWindows.OfType<MessageBoxWindow>().FirstOrDefault());
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void Save_ClassicHoster_EmptyPassword_KeepsOpen_ShowsMessageBox()
    {
        var dlg = new EditAccountWindow(
            new FileHosterLoginDto { FileHosterName = "Rapidgator", AccountType = AccountType.Free }, Hosters);
        try
        {
            dlg.Show();
            Dispatcher.UIThread.RunJobs();

            dlg.UsernameBox.Text = "someone";
            dlg.PasswordBox.Text = string.Empty; // password required
            Click(dlg.SaveButton);
            Dispatcher.UIThread.RunJobs();

            Assert.True(dlg.IsVisible);
            Assert.NotNull(dlg.OwnedWindows.OfType<MessageBoxWindow>().FirstOrDefault());
        }
        finally
        {
            dlg.Close();
        }
    }

    // ── Nullable ctor, cancel, titles, masking ──────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void NullInteractiveLogin_DisablesSignIn_ShowsUnavailableHint()
    {
        // Null callback = the shot-drivers' state: Sign-in disabled with the unavailable hint (WPF contract).
        var dlg = new EditAccountWindow(
            new FileHosterLoginDto { FileHosterName = "KatFile", AccountType = AccountType.Free }, Hosters, interactiveLogin: null);
        try
        {
            Assert.False(dlg.SignInButton.IsEnabled);
            Assert.Equal(Localizer.Instance["EditAccount_SignIn_Unavailable"], dlg.SignInStatus.Text);
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public async Task CancelButton_ReturnsNull()
    {
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditAccountWindow(
            new FileHosterLoginDto { FileHosterName = "Rapidgator", AccountType = AccountType.Free }, Hosters);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            Task<FileHosterLoginDto?> dialog = dlg.ShowDialog<FileHosterLoginDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            Click(dlg.CancelButton); // CancelButton_Click → Close(null)
            Dispatcher.UIThread.RunJobs();

            FileHosterLoginDto? result = await dialog;
            Assert.Null(result);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void DefaultTitle_IsEditModeTitle_DistinctFromAddTitle()
    {
        // The XAML default title IS the edit-mode title: ShowEditAccountDialogAsync's null-title path leaves
        // it in place, while ShowAddAccountDialogAsync always overrides with the (distinct) add title.
        var dlg = new EditAccountWindow(
            new FileHosterLoginDto { Id = 5, FileHosterName = "Rapidgator", AccountType = AccountType.Free }, Hosters);
        try
        {
            Assert.Equal(Localizer.Instance["EditAccount_WindowTitle"], dlg.Title);
            Assert.NotEqual(Localizer.Instance["EditAccount_AddTitle"], dlg.Title);
        }
        finally
        {
            dlg.Close();
        }
    }

    [AvaloniaFact]
    public void PasswordAndApiKeyBoxes_AreMasked()
    {
        // Prep item 9: BOTH secret boxes mask (the recorded deviation from WPF's cleartext) — the only
        // masking lever, since the boxes are populated from code-behind with no VM binding.
        var dlg = new EditAccountWindow(
            new FileHosterLoginDto { FileHosterName = "KatFile", AccountType = AccountType.Free }, Hosters);
        try
        {
            Assert.Equal('●', dlg.PasswordBox.PasswordChar);
            Assert.Equal('●', dlg.ApiKeyBox.PasswordChar);
        }
        finally
        {
            dlg.Close();
        }
    }
}
