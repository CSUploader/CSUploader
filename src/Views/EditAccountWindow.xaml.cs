// <copyright file="EditAccountWindow.xaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CSUploader.Converters;
using CSUploader.Dal;
using CSUploader.Lib.Localization;
using CSUploader.Upload;

namespace CSUploader.Views;

public partial class EditAccountWindow : Window
{
    /// <summary>
    /// Hoster names whose pipeline authenticates via the XFileSharing REST API. For these
    /// the dialog hides username/password entirely — the real sign-in is a captcha WebView
    /// behind the "Sign in" button, after which we derive the account's API key from its
    /// my_account page. The user can alternatively paste an API key directly.
    /// </summary>
    // "FlashBit" intentionally absent — DISABLED 2026-06-05 (invalid SSL on
    // fs*.flashbit.cc + IIS chunk-size cap). Pipeline DI + FileHosters registry are
    // both commented out alongside this; see FlashBitPipeline.cs class-level remarks
    // for the diagnosis chain. Do NOT re-add without re-enabling those first.
    // "ExtMatrix" intentionally absent — DISABLED 2026-06-07. /api/upload.php gets
    // 413 below ~27 MiB and we can't capture their web UI's chunked protocol because
    // the web UI is also failing for our test user. See ExtMatrixPipeline.cs class-
    // level remarks for the diagnosis chain and the re-enable checklist.
    // "Hotlink" intentionally absent — DISABLED 2026-06-23. hotlink.cc free accounts can't
    // upload and its XFileSharing Pro per-user API key is never rendered, so there is no usable
    // api-key flow. See HotlinkPipeline.cs class-level remarks for the diagnosis + re-enable checklist.
    private static readonly HashSet<string> ApiKeyHosters =
        new(StringComparer.OrdinalIgnoreCase) { "Ex-Load", "KatFile", "TakeFile", "Hexload", "Hxfile", "FileBoom", "HitFile" };

    private readonly FileHosterLoginDto _original;

    /// <summary>
    /// Runs the interactive (WebView) sign-in for the given hoster and returns the result —
    /// the same flow the Settings "Refresh" uses. Null in degenerate contexts (no verifier
    /// wired); the Sign-in button is disabled when null.
    /// </summary>
    private readonly Func<string, Task<AccountCheckResult>>? _interactiveLogin;

    /// <summary>Username discovered by a successful Sign-in (the account email). Applied to
    /// the saved DTO so the grid shows something meaningful for API-key accounts.</summary>
    private string? _derivedUsername;

    /// <summary>Storage usage captured by a successful Sign-in, or carried over from the
    /// existing account when editing without re-signing-in. Written to the saved DTO so a
    /// hoster whose refresh can't re-read storage (HitFile — its appId is an upload token,
    /// not a session, so it can't reach the logged-in storage API) keeps the figure instead
    /// of blanking it on the post-save re-verify. Only overwritten by a Sign-in that actually
    /// reports a value, so a failed/partial walk never clobbers a good number.</summary>
    private long? _storageUsedBytes;
    private long? _storageQuotaBytes;

    /// <summary>Login session captured by a successful Sign-in (HitFile's <c>.hitfile.net</c>
    /// cookie jar), or carried from the existing account. Persisted so "Check / Refresh" can
    /// re-read server-side data (storage usage) through the proxy without re-opening the WebView.
    /// Only overwritten by a Sign-in that actually captured one.</summary>
    private string? _sessionCookie;

    /// <summary>"Added at" stamp carried from the existing account so an edit-Save preserves it
    /// (it's set once at insert by the add flow; null for a brand-new account).</summary>
    private DateTime? _createdDateTime;

    public EditAccountWindow(FileHosterLoginDto account, string[] hosters, Func<string, Task<AccountCheckResult>>? interactiveLogin = null)
    {
        InitializeComponent();

        _original = account;
        _interactiveLogin = interactiveLogin;

        if (account.Id == 0)
        {
            HosterCombo.ItemsSource = hosters;
            HosterCombo.SelectedItem = account.FileHosterName;
            HosterCombo.SelectionChanged += (_, _) => RefreshCredentialMode();
        }
        else
        {
            // Lock hoster for existing accounts: show as read-only text + icon, mirroring
            // the ComboBox's templated row so the locked alternative doesn't look bare.
            HosterCombo.Visibility = Visibility.Collapsed;
            HosterLocked.Visibility = Visibility.Visible;
            HosterLockedText.Text = account.FileHosterName;
            HosterLockedIcon.Source = new HosterIconConverter()
                .Convert(account.FileHosterName ?? string.Empty, typeof(BitmapImage), null!, CultureInfo.CurrentCulture) as BitmapImage;
        }

        UsernameBox.Text = account.Username;
        PasswordBox.Text = account.Password;
        ApiKeyBox.Text = account.ApiKey;
        _derivedUsername = string.IsNullOrEmpty(account.Username) ? null : account.Username;
        _storageUsedBytes = account.StorageUsedBytes;
        _storageQuotaBytes = account.StorageQuotaBytes;
        _sessionCookie = string.IsNullOrEmpty(account.SessionCookie) ? null : account.SessionCookie;
        _createdDateTime = account.CreatedDateTime;

        EnabledCheck.IsChecked = !account.Disabled;

        RefreshCredentialMode();
    }

    public FileHosterLoginDto? Result { get; private set; }

    private string? CurrentHoster()
        => HosterCombo.Visibility == Visibility.Visible
            ? HosterCombo.SelectedItem as string
            : _original.FileHosterName;

    private bool IsApiKeyHoster()
    {
        string? hoster = CurrentHoster();
        return hoster is not null && ApiKeyHosters.Contains(hoster);
    }

    /// <summary>
    /// Toggles the two credential modes by hoster type. Classic U/P hosters show
    /// editable Username + Password boxes; API-key hosters hide both rows and surface
    /// the Sign-in button + API key textbox instead. The captured identity for API-key
    /// hosters is shown in the SignInStatus text below the button ("✓ Signed in as X")
    /// — no dedicated username row needed. Collapsed Auto rows take zero height, so
    /// the dialog tightens up regardless of state.
    /// </summary>
    private void RefreshCredentialMode()
    {
        bool api = IsApiKeyHoster();
        Visibility classic = api ? Visibility.Collapsed : Visibility.Visible;
        Visibility key = api ? Visibility.Visible : Visibility.Collapsed;

        UsernameLabel.Visibility = classic;
        UsernameBox.Visibility = classic;
        PasswordLabel.Visibility = classic;
        PasswordBox.Visibility = classic;

        SignInLabel.Visibility = key;
        SignInRow.Visibility = key;
        OrSeparator.Visibility = key;
        ApiKeyLabel.Visibility = key;
        ApiKeyBox.Visibility = key;

        // Sign-in needs the interactive callback; disable it (with a hint) when unavailable.
        SignInButton.IsEnabled = _interactiveLogin is not null;
        if (_interactiveLogin is null && api)
        {
            SignInStatus.Text = Localizer.Instance["EditAccount_SignIn_Unavailable"];
        }
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (_interactiveLogin is null)
        {
            return;
        }

        string? hoster = CurrentHoster();
        if (string.IsNullOrEmpty(hoster))
        {
            return;
        }

        // Guard against double-clicks re-entering while the WebView is open.
        SignInButton.IsEnabled = false;
        SignInStatus.Text = Localizer.Instance["EditAccount_SignIn_InProgress"];
        try
        {
            AccountCheckResult result = await _interactiveLogin(hoster);

            if (result.IsValid && !string.IsNullOrEmpty(result.ApiKey))
            {
                // Surface the derived key in the box (single source of truth on Save) and
                // remember the discovered username + storage usage for the saved DTO.
                ApiKeyBox.Text = result.ApiKey;
                _derivedUsername = result.DerivedUsername ?? _derivedUsername;
                if (result.StorageUsedBytes is { } used) { _storageUsedBytes = used; }
                if (result.StorageQuotaBytes is { } quota) { _storageQuotaBytes = quota; }
                if (!string.IsNullOrEmpty(result.SessionCookie)) { _sessionCookie = result.SessionCookie; }

                SignInStatus.Text = !string.IsNullOrEmpty(result.DerivedUsername)
                    ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["EditAccount_SignIn_SuccessAs_Format"], result.DerivedUsername)
                    : Localizer.Instance["EditAccount_SignIn_Success"];
                SignInStatus.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
            }
            else
            {
                SignInStatus.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    Localizer.Instance["EditAccount_SignIn_Failed_Format"],
                    result.Message ?? Localizer.Instance["EditAccount_SignIn_FailedGeneric"]);
                SignInStatus.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
            }
        }
        catch (Exception ex)
        {
            SignInStatus.Text = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["EditAccount_SignIn_Failed_Format"], ex.Message);
            SignInStatus.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string? hoster = CurrentHoster();

        if (IsApiKeyHoster())
        {
            // The API key is the single credential — either pasted manually or derived by
            // a successful Sign-in (which fills ApiKeyBox). Require one of those.
            string apiKey = ApiKeyBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(apiKey))
            {
                MessageBox.Show(
                    this,
                    Localizer.Instance["EditAccount_Validation_RequireLoginOrApiKey"],
                    Localizer.Instance["Common_Error"],
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                ApiKeyBox.Focus();
                return;
            }

            // Username for API-key accounts comes exclusively from the WebView2 sign-in
            // scrape (held in _derivedUsername). No manual entry path — paste-only
            // accounts persist a null Username until the user signs in, at which point
            // the captured identity is written here.
            //
            // CurrentHoster() is non-null whenever IsApiKeyHoster() returned true (the
            // set lookup at line ~91 can't succeed on null).
            Result = new FileHosterLoginDto
            {
                Id = _original.Id,
                FileHosterName = hoster!,
                Username = _derivedUsername,
                Password = string.Empty,
                ApiKey = apiKey,
                AccountType = _original.AccountType,
                Disabled = EnabledCheck.IsChecked != true,
                // Persist storage usage captured at Sign-in (or carried from the existing
                // account). The post-save re-verify can't re-read it for HitFile, and
                // ApplySessionCookieIfPresent only overwrites on non-null, so this is the DTO's
                // only chance to hold a usage figure.
                StorageUsedBytes = _storageUsedBytes,
                StorageQuotaBytes = _storageQuotaBytes,
                // Persist the captured login session so "Check / Refresh" can re-read storage
                // server-side (HitFile) without re-opening the WebView.
                SessionCookie = _sessionCookie,
                CreatedDateTime = _createdDateTime,
            };
            DialogResult = true;
            return;
        }

        // Classic username/password hoster.
        string username = UsernameBox.Text?.Trim() ?? string.Empty;
        string password = PasswordBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show(
                this,
                Localizer.Instance["EditAccount_Validation_RequireUsernameAndPassword"],
                Localizer.Instance["Common_Error"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            (string.IsNullOrWhiteSpace(username) ? UsernameBox : PasswordBox).Focus();
            return;
        }

        Result = new FileHosterLoginDto
        {
            Id = _original.Id,
            FileHosterName = hoster!,
            Username = username,
            Password = password,
            ApiKey = null,
            AccountType = _original.AccountType,
            Disabled = EnabledCheck.IsChecked != true,
            // Preserve previously-captured usage + session across an edit — SaveEditedAccountAsync
            // persists this DTO verbatim (no re-verify), so omitting them would blank the grid's
            // Used/Available cells (and drop the session) until the next manual refresh.
            StorageUsedBytes = _storageUsedBytes,
            StorageQuotaBytes = _storageQuotaBytes,
            SessionCookie = _sessionCookie,
            CreatedDateTime = _createdDateTime,
        };
        DialogResult = true;
    }
}
