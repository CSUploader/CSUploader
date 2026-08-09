// <copyright file="EditAccountWindowTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    // ── Reported defects ──────────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task Save_ClassicHoster_KeepsADerivedApiKey()
    {
        // FileMirage (and Pixeldrain) are classic username/password hosters whose actual upload
        // credential is an API key DERIVED at sign-in. Blanking it on an edit leaves an account that
        // looks fine in the grid and cannot upload — FileMirage refuses outright rather than sending
        // the file in as a visitor, so the user just sees every upload fail.
        var seed = new FileHosterLoginDto
        {
            Id = 77,
            FileHosterName = "FileMirage",
            Username = "me@example.com",
            Password = "pw",
            ApiKey = "FAKE-T0KE-N000-DEMO",
            AccountType = AccountType.Free,
        };

        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditAccountWindow(seed, Hosters);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            Task<FileHosterLoginDto?> dialog = dlg.ShowDialog<FileHosterLoginDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            dlg.UsernameBox.Text = "corrected@example.com";
            Click(dlg.SaveButton);
            Dispatcher.UIThread.RunJobs();

            FileHosterLoginDto? result = await dialog;
            Assert.NotNull(result);
            Assert.Equal("corrected@example.com", result!.Username);
            Assert.Equal("FAKE-T0KE-N000-DEMO", result.ApiKey);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    // ── Save checks the credentials before closing ────────────────────────────────────────────────

    /// <summary>A classic username/password account as the Add flow seeds one.</summary>
    private static FileHosterLoginDto AddSeed() => new() { FileHosterName = "Rapidgator", AccountType = AccountType.Free };

    [AvaloniaFact]
    public async Task Save_AGoodCheck_ClosesWithTheCredentialTheCheckDerived()
    {
        // For FileMirage, DropMB and FileCat the check is the only place the upload credential ever
        // exists, so the dialog has to carry the result out with the account.
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditAccountWindow(
            AddSeed(),
            Hosters,
            interactiveLogin: null,
            validateAccount: (_, _) => Task.FromResult(new AccountCheckResult(
                true, AccountType.Premium, "Signed in", ApiKey: "DERIVED-KEY", SessionCookie: "sess-abc")));
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            Task<FileHosterLoginDto?> dialog = dlg.ShowDialog<FileHosterLoginDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            dlg.UsernameBox.Text = "alice";
            dlg.PasswordBox.Text = "pw";
            Click(dlg.SaveButton);

            // The check is awaited, so the close lands a turn or two later.
            for (int i = 0; i < 20 && !dialog.IsCompleted; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            FileHosterLoginDto? result = await dialog;
            Assert.NotNull(result);
            Assert.Equal("DERIVED-KEY", result!.ApiKey);
            Assert.Equal("sess-abc", result.SessionCookie);
            Assert.Equal(AccountType.Premium, result.AccountType);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Save_AFailedCheck_KeepsTheDialogOpenWithTheFieldsIntact()
    {
        // The point of checking here rather than after the dialog closes: a wrong password costs a
        // correction, not everything the user typed.
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditAccountWindow(
            AddSeed(),
            Hosters,
            interactiveLogin: null,
            validateAccount: (_, _) => Task.FromResult(new AccountCheckResult(false, AccountType.Free, "Wrong password")));
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            Task<FileHosterLoginDto?> dialog = dlg.ShowDialog<FileHosterLoginDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            dlg.UsernameBox.Text = "alice";
            dlg.PasswordBox.Text = "wrong";
            Click(dlg.SaveButton);

            for (int i = 0; i < 20; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            // Still open, still holding what was typed, and Save usable again for the retry.
            Assert.False(dialog.IsCompleted);
            Assert.Equal("alice", dlg.UsernameBox.Text);
            Assert.Equal("wrong", dlg.PasswordBox.Text);
            Assert.True(dlg.SaveButton.IsEnabled);
            Assert.False(dlg.CheckingStatus.IsVisible);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Save_WhileChecking_ShowsStatusAndDisablesSave()
    {
        TaskCompletionSource<AccountCheckResult> gate = new();

        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditAccountWindow(
            AddSeed(), Hosters, interactiveLogin: null, validateAccount: (_, _) => gate.Task);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            _ = dlg.ShowDialog<FileHosterLoginDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            Assert.False(dlg.CheckingStatus.IsVisible);

            dlg.UsernameBox.Text = "alice";
            dlg.PasswordBox.Text = "pw";
            Click(dlg.SaveButton);
            Dispatcher.UIThread.RunJobs();

            // While the check is in flight the user can see it is happening and can't start another.
            Assert.True(dlg.CheckingStatus.IsVisible);
            Assert.False(dlg.SaveButton.IsEnabled);

            gate.SetResult(new AccountCheckResult(false, AccountType.Free, "nope"));
            for (int i = 0; i < 20; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            Assert.False(dlg.CheckingStatus.IsVisible);
            Assert.True(dlg.SaveButton.IsEnabled);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Cancel_WhileChecking_StopsTheCheckAndLeavesTheDialogOpen()
    {
        // Cancel means "stop waiting", not "throw away what I typed" — the dialog stays up so the
        // user can edit and try again, and the check is actually cancelled rather than abandoned.
        TaskCompletionSource<AccountCheckResult> never = new();
        CancellationToken observed = default;

        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditAccountWindow(
            AddSeed(),
            Hosters,
            interactiveLogin: null,
            validateAccount: (_, ct) =>
            {
                observed = ct;
                return never.Task.WaitAsync(ct);
            });
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            Task<FileHosterLoginDto?> dialog = dlg.ShowDialog<FileHosterLoginDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            dlg.UsernameBox.Text = "alice";
            dlg.PasswordBox.Text = "pw";
            Click(dlg.SaveButton);
            Dispatcher.UIThread.RunJobs();
            Assert.True(dlg.CheckingStatus.IsVisible);

            Click(dlg.CancelButton);
            for (int i = 0; i < 20; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            Assert.True(observed.IsCancellationRequested);
            Assert.False(dialog.IsCompleted);          // cancelling the CHECK must not close the dialog
            Assert.True(dlg.SaveButton.IsEnabled);
            Assert.False(dlg.CheckingStatus.IsVisible);

            // And with no check running, Cancel goes back to meaning "close".
            Click(dlg.CancelButton);
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.IsCompleted);
            Assert.Null(await dialog);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Save_WithNoValidator_ClosesImmediately()
    {
        // A hoster this app can't check must stay addable.
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditAccountWindow(AddSeed(), Hosters);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            Task<FileHosterLoginDto?> dialog = dlg.ShowDialog<FileHosterLoginDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            dlg.UsernameBox.Text = "alice";
            dlg.PasswordBox.Text = "pw";
            Click(dlg.SaveButton);
            Dispatcher.UIThread.RunJobs();

            Assert.True(dialog.IsCompleted);
            Assert.Equal("alice", (await dialog)!.Username);
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }

    [AvaloniaFact]
    public void Save_LeavesAvaloniaWithNothingToWarnAbout()
    {
        // A dialog can be perfectly functional and still be shouting into the IDE output window, so
        // this asserts it is QUIET as well as correct — the sibling of the BindingErrorSink checks.
        // NOTE it cannot, on its own, catch the reported "[Control] PlatformImpl is null" warning:
        // that comes from TopLevel.HandleInput when raw input reaches a window whose platform handle is
        // gone, and the headless WindowImpl does not drop its handle on close the way Win32 does. It is
        // here for every OTHER warning this view could start emitting.
        using AvaloniaLogSink sink = AvaloniaLogSink.Install();

        var seed = new FileHosterLoginDto { Id = 78, FileHosterName = "FileMirage", Username = "u@e.com", Password = "p" };
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditAccountWindow(seed, Hosters);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            _ = dlg.ShowDialog<FileHosterLoginDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            // Real keyboard activation, not the synthetic Click: a button raises Click from KEY DOWN,
            // which is what puts the close in the middle of an input sequence.
            dlg.SaveButton.Focus();
            Dispatcher.UIThread.RunJobs();
            dlg.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }

        Assert.DoesNotContain(sink.Messages, m => m.Contains("PlatformImpl is null", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Save_DoesNotTearDownTheWindowInsideTheInputEvent()
    {
        // The mechanism behind the reported warning, pinned where the platform can't hide it: after the
        // handler runs, the window must still be open, and only close once the dispatcher gets a turn.
        // Closing inline leaves Avalonia routing the rest of that key press at a dead window.
        var seed = new FileHosterLoginDto { Id = 79, FileHosterName = "FileMirage", Username = "u@e.com", Password = "p" };
        var owner = new Window { Width = 200, Height = 200 };
        var dlg = new EditAccountWindow(seed, Hosters);
        try
        {
            owner.Show();
            Dispatcher.UIThread.RunJobs();
            Task<FileHosterLoginDto?> dialog = dlg.ShowDialog<FileHosterLoginDto?>(owner);
            Dispatcher.UIThread.RunJobs();

            Click(dlg.SaveButton);
            Assert.False(dialog.IsCompleted);   // still open — the close was deferred

            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.IsCompleted);    // and lands on the next dispatcher pass
        }
        finally
        {
            dlg.Close();
            owner.Close();
        }
    }
}
