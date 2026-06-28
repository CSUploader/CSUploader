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
    // "TakeFile" DISABLED 2026-06-28 (Cloudflare managed-challenge TLS wall — see TakeFilePipeline.cs);
    // removed here alongside its registry + DI entries.
    private static readonly HashSet<string> ApiKeyHosters =
        new(StringComparer.OrdinalIgnoreCase) { "Ex-Load", "KatFile", "Hexload", "Hxfile", "FileBoom", "HitFile", "Keep2Share", "TezFiles", "NitroFlare" };

    /// <summary>
    /// WebView-sign-in hosters whose ONLY credential is the captured session cookie — there is no
    /// API key to paste (isra.cloud is a classic XFileSharing host that exposes no REST API). The
    /// dialog shows them the same Sign-in button as <see cref="ApiKeyHosters"/> but HIDES the
    /// "OR paste an API key" box, and keys sign-in success / Save on the captured cookie instead of
    /// an API key.
    /// </summary>
    private static readonly HashSet<string> SessionCookieHosters =
        new(StringComparer.OrdinalIgnoreCase) { "Isracloud" };

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

    /// <summary>Expiry + issuing-proxy pin that travel WITH <see cref="_sessionCookie"/>. Carried so an
    /// edit-Save (which persists this DTO verbatim — no re-verify) preserves them: the web-form upload
    /// path treats a cookie with a null/expired <c>SessionCookieExpiresUtc</c> as not-signed-in and
    /// would pop a needless WebView, and dropping the pin would unbind a proxy-issued session. Only
    /// overwritten by a Sign-in that actually captured a cookie.</summary>
    private DateTime? _sessionCookieExpiresUtc;
    private int? _pinnedProxyId;

    /// <summary>"Added at" stamp carried from the existing account so an edit-Save preserves it
    /// (it's set once at insert by the add flow; null for a brand-new account).</summary>
    private DateTime? _createdDateTime;

    /// <summary>Full text of the last sign-in failure (summary plus any raw response body),
    /// stashed for the "Details" link. The status row only shows a height-capped preview so a
    /// verbose message can't grow the fixed-size window; the complete text is shown here.</summary>
    private string? _lastSignInError;

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
        _sessionCookieExpiresUtc = account.SessionCookieExpiresUtc;
        _pinnedProxyId = account.PinnedProxyId;
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

    /// <summary>WebView-sign-in hoster whose only credential is the session cookie (no pasteable
    /// API key) — see <see cref="SessionCookieHosters"/>.</summary>
    private bool IsSessionCookieHoster()
    {
        string? hoster = CurrentHoster();
        return hoster is not null && SessionCookieHosters.Contains(hoster);
    }

    /// <summary>Either WebView-sign-in family (API-key or session-cookie): both hide username/password
    /// and surface the Sign-in button.</summary>
    private bool IsWebViewSignInHoster() => IsApiKeyHoster() || IsSessionCookieHoster();

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
        bool webView = IsWebViewSignInHoster();
        bool sessionCookieOnly = IsSessionCookieHoster();
        Visibility classic = webView ? Visibility.Collapsed : Visibility.Visible;
        Visibility signIn = webView ? Visibility.Visible : Visibility.Collapsed;
        // The "OR paste an API key" affordance applies only to hosters that actually have a
        // pasteable key — session-cookie hosters (isra) sign in and nothing else.
        Visibility apiKey = webView && !sessionCookieOnly ? Visibility.Visible : Visibility.Collapsed;

        UsernameLabel.Visibility = classic;
        UsernameBox.Visibility = classic;
        PasswordLabel.Visibility = classic;
        PasswordBox.Visibility = classic;

        SignInLabel.Visibility = signIn;
        SignInRow.Visibility = signIn;
        OrSeparator.Visibility = apiKey;
        ApiKeyLabel.Visibility = apiKey;
        ApiKeyBox.Visibility = apiKey;

        // Sign-in needs the interactive callback; disable it (with a hint) when unavailable.
        SignInButton.IsEnabled = _interactiveLogin is not null;

        // Reset the sign-in feedback to a clean status. Both branches route through
        // ShowSignInStatus, which clears any leftover "✓ Signed in" / "Error: …" (and the stashed
        // error detail) — that feedback is per-hoster and must not carry over when the combo
        // switches to a different one.
        if (_interactiveLogin is null && webView)
        {
            ShowSignInStatus(Localizer.Instance["EditAccount_SignIn_Unavailable"], "TextSecondaryBrush");
        }
        else
        {
            ShowSignInStatus(string.Empty, "TextSecondaryBrush");
        }
    }

    /// <summary>
    /// Shows a short status message in the sign-in row (in-progress / success / unavailable) and
    /// hides the error panel — the status text and the error panel share the row's status cell,
    /// and only one is ever visible.
    /// </summary>
    private void ShowSignInStatus(string text, string brushResourceKey)
    {
        _lastSignInError = null;
        SignInErrorPanel.Visibility = Visibility.Collapsed;
        SignInStatus.Visibility = Visibility.Visible;
        SignInStatus.Text = text;
        SignInStatus.Foreground = (System.Windows.Media.Brush)FindResource(brushResourceKey);
    }

    /// <summary>
    /// Shows a sign-in failure as a compact, height-capped "Error: …" line plus a Details link,
    /// in place of the status text. The short <paramref name="message"/> goes on the line; the
    /// fuller <paramref name="detail"/> (when the verifier supplies one — e.g. the complete
    /// my_account response, which is far too large for this fixed-size window) is stashed for the
    /// Details dialog. Falls back to <paramref name="message"/> when there's no extra detail.
    /// </summary>
    private void ShowSignInError(string message, string? detail = null)
    {
        _lastSignInError = string.IsNullOrEmpty(detail) ? message : detail;
        SignInStatus.Visibility = Visibility.Collapsed;
        SignInErrorPanel.Visibility = Visibility.Visible;
        SignInErrorText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0}: {1}",
            Localizer.Instance["Common_Error"],
            message);
    }

    private void ErrorDetails_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastSignInError))
        {
            return;
        }

        new ErrorDetailsWindow(_lastSignInError) { Owner = this }.ShowDialog();
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
        ShowSignInStatus(Localizer.Instance["EditAccount_SignIn_InProgress"], "TextSecondaryBrush");
        try
        {
            AccountCheckResult result = await _interactiveLogin(hoster);

            if (result.IsValid && (!string.IsNullOrEmpty(result.ApiKey) || !string.IsNullOrEmpty(result.SessionCookie)))
            {
                // The credential is an API key (most XFS hosters) OR a session cookie (isra). Surface
                // a derived key in the box when present (single source of truth on Save); for a
                // session-cookie hoster there's no key, and the captured cookie is stashed below.
                if (!string.IsNullOrEmpty(result.ApiKey))
                {
                    ApiKeyBox.Text = result.ApiKey;
                }
                _derivedUsername = result.DerivedUsername ?? _derivedUsername;
                if (result.StorageUsedBytes is { } used) { _storageUsedBytes = used; }
                if (result.StorageQuotaBytes is { } quota) { _storageQuotaBytes = quota; }
                if (!string.IsNullOrEmpty(result.SessionCookie))
                {
                    // Capture the cookie together with its expiry + proxy pin, so Save persists a
                    // usable session (the web-form upload path gates on a non-null future expiry).
                    _sessionCookie = result.SessionCookie;
                    _sessionCookieExpiresUtc = result.SessionCookieExpiresUtc;
                    _pinnedProxyId = result.PinnedProxyId;
                }

                string successText = !string.IsNullOrEmpty(result.DerivedUsername)
                    ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["EditAccount_SignIn_SuccessAs_Format"], result.DerivedUsername)
                    : Localizer.Instance["EditAccount_SignIn_Success"];
                ShowSignInStatus(successText, "SuccessBrush");
            }
            else
            {
                // Show a capped "Error: …" line with a Details link; the verifier's full Detail
                // (e.g. the complete my_account response) opens in the Details dialog rather than
                // growing this fixed-size window.
                ShowSignInError(result.Message ?? Localizer.Instance["EditAccount_SignIn_FailedGeneric"], result.Detail);
            }
        }
        catch (Exception ex)
        {
            ShowSignInError(ex.Message);
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string? hoster = CurrentHoster();

        if (IsWebViewSignInHoster())
        {
            // The credential is either a pasted/derived API key (most XFS hosters) or the session
            // cookie captured by Sign-in (isra, which has no pasteable key). Require one of them —
            // for a session-cookie hoster the ApiKeyBox is hidden so apiKey is always empty there.
            string apiKey = ApiKeyBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(apiKey) && string.IsNullOrEmpty(_sessionCookie))
            {
                MessageBox.Show(
                    this,
                    Localizer.Instance["EditAccount_Validation_RequireLoginOrApiKey"],
                    Localizer.Instance["Common_Error"],
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                // Session-cookie hosters have no ApiKeyBox to focus — point at the Sign-in button.
                if (IsSessionCookieHoster())
                {
                    SignInButton.Focus();
                }
                else
                {
                    ApiKeyBox.Focus();
                }
                return;
            }

            // Username for WebView-sign-in accounts comes exclusively from the sign-in scrape
            // (held in _derivedUsername). No manual entry path — paste-only accounts persist a null
            // Username until the user signs in, at which point the captured identity is written here.
            //
            // CurrentHoster() is non-null whenever IsWebViewSignInHoster() returned true (the
            // set lookup can't succeed on null).
            Result = new FileHosterLoginDto
            {
                Id = _original.Id,
                FileHosterName = hoster!,
                Username = _derivedUsername,
                Password = string.Empty,
                // Null (not empty) when there's no key, so the DTO reads cleanly for session-cookie
                // hosters whose credential lives in SessionCookie below.
                ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey,
                AccountType = _original.AccountType,
                Disabled = EnabledCheck.IsChecked != true,
                // Persist storage usage captured at Sign-in (or carried from the existing
                // account). The post-save re-verify can't re-read it for HitFile, and
                // ApplySessionCookieIfPresent only overwrites on non-null, so this is the DTO's
                // only chance to hold a usage figure.
                StorageUsedBytes = _storageUsedBytes,
                StorageQuotaBytes = _storageQuotaBytes,
                // Persist the captured login session — with its expiry + proxy pin — so "Check /
                // Refresh" and the web-form upload path can reuse it without re-opening the WebView.
                SessionCookie = _sessionCookie,
                SessionCookieExpiresUtc = _sessionCookieExpiresUtc,
                PinnedProxyId = _pinnedProxyId,
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
            SessionCookieExpiresUtc = _sessionCookieExpiresUtc,
            PinnedProxyId = _pinnedProxyId,
            CreatedDateTime = _createdDateTime,
        };
        DialogResult = true;
    }
}
