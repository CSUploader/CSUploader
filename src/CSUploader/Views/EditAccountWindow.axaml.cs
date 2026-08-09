// <copyright file="EditAccountWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CSUploader.Converters;
using CSUploader.Dal;
using CSUploader.Lib.Localization;
using CSUploader.Upload;

namespace CSUploader.Views;

/// <summary>
/// Modal dialog for adding (and editing) a single file-hoster account (port of the WPF
/// <c>EditAccountWindow</c>). The code-behind is the WPF one ported near-verbatim: the same three credential
/// modes (classic username/password, API-key sign-in, session-cookie sign-in) toggled by
/// <see cref="HosterCredentialModes"/>, the same interactive Sign-in flow (re-entry guard, in-progress /
/// success / error status swap, Details link → <see cref="ErrorDetailsWindow"/>), and the same carry-field
/// set on Save (username, storage, session cookie + expiry + proxy pin, created stamp — dropping any of them
/// silently blanks account data on the next edit/refresh). The Avalonia deltas: the result is carried through
/// <c>ShowDialog&lt;FileHosterLoginDto?&gt;</c> — Save-valid → <see cref="Window.Close(object?)"/> with the
/// DTO, Cancel/Esc/X → <c>Close(null)</c> — collapsing the WPF <c>Result</c> + <c>DialogResult</c> pair
/// (rule 6); Esc reaches Cancel through <c>IsCancel</c> which no longer auto-closes, so the handler closes
/// explicitly (rule 7); the sign-in status colour is a toggled class, not a code-behind brush swap (rule 29);
/// the Details affordance is a <c>Classes="link"</c> TextBlock with a <c>PointerReleased</c> handler
/// (rule 28); validation warnings show through the async <see cref="MessageBoxWindow"/> (so the Save/Sign-in
/// handlers are <c>async void</c>); and the Password AND ApiKey boxes mask via <c>PasswordChar</c> (prep
/// item 9).
/// </summary>
public partial class EditAccountWindow : Window
{
    private readonly FileHosterLoginDto _original;

    /// <summary>
    /// Runs the interactive (WebView) sign-in for the given hoster and returns the result — the same flow
    /// the Settings "Refresh" uses. Null in degenerate contexts (no verifier wired, and the shot drivers):
    /// the Sign-in button is disabled when null.
    /// </summary>
    private readonly Func<string, Task<AccountCheckResult>>? _interactiveLogin;

    /// <summary>Username discovered by a successful Sign-in (the account email). Applied to the saved DTO so
    /// the grid shows something meaningful for API-key accounts.</summary>
    private string? _derivedUsername;

    /// <summary>Storage usage captured by a successful Sign-in, or carried over from the existing account
    /// when editing without re-signing-in. Written to the saved DTO so a hoster whose refresh can't re-read
    /// storage (HitFile — its appId is an upload token, not a session, so it can't reach the logged-in
    /// storage API) keeps the figure instead of blanking it on the post-save re-verify. Only overwritten by a
    /// Sign-in that actually reports a value, so a failed/partial walk never clobbers a good number.</summary>
    private long? _storageUsedBytes;
    private long? _storageQuotaBytes;

    /// <summary>Login session captured by a successful Sign-in (HitFile's <c>.hitfile.net</c> cookie jar), or
    /// carried from the existing account. Persisted so "Check / Refresh" can re-read server-side data
    /// (storage usage) through the proxy without re-opening the WebView. Only overwritten by a Sign-in that
    /// actually captured one.</summary>
    private string? _sessionCookie;

    /// <summary>Expiry + issuing-proxy pin that travel WITH <see cref="_sessionCookie"/>. Carried so an
    /// edit-Save (which persists this DTO verbatim — no re-verify) preserves them: the web-form upload path
    /// treats a cookie with a null/expired <c>SessionCookieExpiresUtc</c> as not-signed-in and would pop a
    /// needless WebView, and dropping the pin would unbind a proxy-issued session. Only overwritten by a
    /// Sign-in that actually captured a cookie.</summary>
    private DateTime? _sessionCookieExpiresUtc;
    private int? _pinnedProxyId;

    /// <summary>"Added at" stamp carried from the existing account so an edit-Save preserves it (it's set
    /// once at insert by the add flow; null for a brand-new account).</summary>
    private readonly DateTime? _createdDateTime;

    /// <summary>Full text of the last sign-in failure (summary plus any raw response body), stashed for the
    /// "Details" link. The status row only shows a height-capped preview so a verbose message can't grow the
    /// fixed-size window; the complete text is shown here.</summary>
    private string? _lastSignInError;

    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001); the app always uses the
    // account/hosters/login overload. Seeds an empty add-mode account so the ctor's seeding is null-safe.
    public EditAccountWindow()
        : this(new FileHosterLoginDto { AccountType = AccountType.Free }, [])
    {
    }

    /// <summary>
    /// Signs in with the credentials as entered, so Save can prove them before the dialog closes.
    /// Null when the caller can't check (no verifier, or a hoster with no pipeline), in which case
    /// Save behaves as it always did and closes immediately.
    /// </summary>
    private readonly Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>? _validateAccount;

    /// <summary>Live only while a check is running. Its existence is what makes Cancel mean "stop
    /// checking" rather than "close the dialog".</summary>
    private CancellationTokenSource? _checkCts;

    public EditAccountWindow(
        FileHosterLoginDto account,
        string[] hosters,
        Func<string, Task<AccountCheckResult>>? interactiveLogin = null,
        Func<FileHosterLoginDto, CancellationToken, Task<AccountCheckResult>>? validateAccount = null)
    {
        InitializeComponent();

        _original = account;
        _interactiveLogin = interactiveLogin;
        _validateAccount = validateAccount;

        if (account.Id == 0)
        {
            HosterCombo.ItemsSource = hosters;
            HosterCombo.SelectedItem = account.FileHosterName;
            HosterCombo.SelectionChanged += (_, _) => RefreshCredentialMode();
        }
        else
        {
            // Lock hoster for existing accounts: show as read-only text + icon, mirroring the ComboBox's
            // templated row so the locked alternative doesn't look bare.
            HosterCombo.IsVisible = false;
            HosterLocked.IsVisible = true;
            HosterLockedText.Text = account.FileHosterName;
            HosterLockedIcon.Source = new HosterIconConverter()
                .Convert(account.FileHosterName ?? string.Empty, typeof(Bitmap), null, CultureInfo.CurrentCulture) as Bitmap;
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

    private string? CurrentHoster()
        => HosterCombo.IsVisible
            ? HosterCombo.SelectedItem as string
            : _original.FileHosterName;

    private bool IsApiKeyHoster() => HosterCredentialModes.IsApiKeyHoster(CurrentHoster());

    /// <summary>WebView-sign-in hoster whose only credential is the session cookie (no pasteable API key) —
    /// see <see cref="HosterCredentialModes"/>.</summary>
    private bool IsSessionCookieHoster() => HosterCredentialModes.IsSessionCookieHoster(CurrentHoster());

    /// <summary>Either WebView-sign-in family (API-key or session-cookie): both hide username/password and
    /// surface the Sign-in button.</summary>
    private bool IsWebViewSignInHoster() => IsApiKeyHoster() || IsSessionCookieHoster();

    /// <summary>
    /// Toggles the credential modes by hoster type. Classic U/P hosters show editable Username + Password
    /// boxes; API-key hosters hide both rows and surface the Sign-in button + API key textbox instead;
    /// session-cookie hosters surface only the Sign-in button (no pasteable key). The captured identity for
    /// WebView hosters is shown in the SignInStatus text below the button ("✓ Signed in as X"). Collapsed
    /// Auto rows take zero height, so the dialog tightens up regardless of state.
    /// </summary>
    private void RefreshCredentialMode()
    {
        bool webView = IsWebViewSignInHoster();
        bool sessionCookieOnly = IsSessionCookieHoster();
        bool classic = !webView;
        // The "OR paste an API key" affordance applies only to hosters that actually have a pasteable key —
        // session-cookie hosters (isra) sign in and nothing else.
        bool apiKey = webView && !sessionCookieOnly;

        UsernameLabel.IsVisible = classic;
        UsernameBox.IsVisible = classic;
        PasswordLabel.IsVisible = classic;
        PasswordBox.IsVisible = classic;

        SignInLabel.IsVisible = webView;
        SignInRow.IsVisible = webView;
        OrSeparator.IsVisible = apiKey;
        ApiKeyLabel.IsVisible = apiKey;
        ApiKeyBox.IsVisible = apiKey;

        // Sign-in needs the interactive callback; disable it (with a hint) when unavailable.
        SignInButton.IsEnabled = _interactiveLogin is not null;

        // Reset the sign-in feedback to a clean status. Both branches route through ShowSignInStatus, which
        // clears any leftover "✓ Signed in" / "Error: …" (and the stashed error detail) — that feedback is
        // per-hoster and must not carry over when the combo switches to a different one.
        if (_interactiveLogin is null && webView)
        {
            ShowSignInStatus(Localizer.Instance["EditAccount_SignIn_Unavailable"], success: false);
        }
        else
        {
            ShowSignInStatus(string.Empty, success: false);
        }
    }

    /// <summary>
    /// Shows a short status message in the sign-in row (in-progress / success / unavailable) and hides the
    /// error panel — the status text and the error panel share the row's status cell, and only one is ever
    /// visible. The colour is a toggled <c>success</c> class (rule 29): SuccessBrush when success, else the
    /// muted default.
    /// </summary>
    private void ShowSignInStatus(string text, bool success)
    {
        _lastSignInError = null;
        SignInErrorPanel.IsVisible = false;
        SignInStatus.IsVisible = true;
        SignInStatus.Text = text;
        SignInStatus.Classes.Set("success", success);
    }

    /// <summary>
    /// Shows a sign-in failure as a compact, height-capped "Error: …" line plus a Details link, in place of
    /// the status text. The short <paramref name="message"/> goes on the line; the fuller
    /// <paramref name="detail"/> (when the verifier supplies one — e.g. the complete my_account response,
    /// which is far too large for this fixed-size window) is stashed for the Details dialog. Falls back to
    /// <paramref name="message"/> when there's no extra detail.
    /// </summary>
    private void ShowSignInError(string message, string? detail = null)
    {
        _lastSignInError = string.IsNullOrEmpty(detail) ? message : detail;
        SignInStatus.IsVisible = false;
        SignInErrorPanel.IsVisible = true;
        SignInErrorText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0}: {1}",
            Localizer.Instance["Common_Error"],
            message);
    }

    // Rule 28/10: the Details "link" is a TextBlock, not a Hyperlink — open on a left-button release only.
    private void ErrorDetailsLink_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        ShowErrorDetails();
        e.Handled = true;
    }

    /// <summary>
    /// Opens the full last-sign-in-error text in a modal <see cref="ErrorDetailsWindow"/> and returns it (or
    /// null when there's nothing to show). Internal so the headless tests can verify the Details path without
    /// synthesizing a real pointer release on the link (the Phase 4 §8 sanctioned fallback).
    /// </summary>
    internal ErrorDetailsWindow? ShowErrorDetails()
    {
        if (string.IsNullOrEmpty(_lastSignInError))
        {
            return null;
        }

        ErrorDetailsWindow window = new(_lastSignInError);
        _ = window.ShowDialog(this);
        return window;
    }

    private async void SignInButton_Click(object? sender, RoutedEventArgs e)
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
        ShowSignInStatus(Localizer.Instance["EditAccount_SignIn_InProgress"], success: false);
        try
        {
            AccountCheckResult result = await _interactiveLogin(hoster);

            if (result.IsValid && (!string.IsNullOrEmpty(result.ApiKey) || !string.IsNullOrEmpty(result.SessionCookie)))
            {
                // The credential is an API key (most XFS hosters) OR a session cookie (isra). Surface a
                // derived key in the box when present (single source of truth on Save); for a session-cookie
                // hoster there's no key, and the captured cookie is stashed below.
                if (!string.IsNullOrEmpty(result.ApiKey))
                {
                    ApiKeyBox.Text = result.ApiKey;
                }

                _derivedUsername = result.DerivedUsername ?? _derivedUsername;
                if (result.StorageUsedBytes is { } used)
                {
                    _storageUsedBytes = used;
                }

                if (result.StorageQuotaBytes is { } quota)
                {
                    _storageQuotaBytes = quota;
                }

                if (!string.IsNullOrEmpty(result.SessionCookie))
                {
                    // Capture the cookie together with its expiry + proxy pin, so Save persists a usable
                    // session (the web-form upload path gates on a non-null future expiry).
                    _sessionCookie = result.SessionCookie;
                    _sessionCookieExpiresUtc = result.SessionCookieExpiresUtc;
                    _pinnedProxyId = result.PinnedProxyId;
                }

                string successText = !string.IsNullOrEmpty(result.DerivedUsername)
                    ? string.Format(CultureInfo.CurrentCulture, Localizer.Instance["EditAccount_SignIn_SuccessAs_Format"], result.DerivedUsername)
                    : Localizer.Instance["EditAccount_SignIn_Success"];
                ShowSignInStatus(successText, success: true);
            }
            else
            {
                // Show a capped "Error: …" line with a Details link; the verifier's full Detail (e.g. the
                // complete my_account response) opens in the Details dialog rather than growing this
                // fixed-size window.
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

    // async void because validation may await the custom message box (the WPF SpeedLimitDialog port's shape).
    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        string? hoster = CurrentHoster();

        if (IsWebViewSignInHoster())
        {
            // The credential is either a pasted/derived API key (most XFS hosters) or the session cookie
            // captured by Sign-in (isra, which has no pasteable key). Require one of them — for a
            // session-cookie hoster the ApiKeyBox is hidden so apiKey is always empty there.
            string apiKey = ApiKeyBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(apiKey) && string.IsNullOrEmpty(_sessionCookie))
            {
                await MessageBoxWindow.ShowWarningAsync(
                    this,
                    Localizer.Instance["EditAccount_Validation_RequireLoginOrApiKey"],
                    Localizer.Instance["Common_Error"]);

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

            // Username for WebView-sign-in accounts comes exclusively from the sign-in scrape (held in
            // _derivedUsername). No manual entry path — paste-only accounts persist a null Username until the
            // user signs in, at which point the captured identity is written here. CurrentHoster() is non-null
            // whenever IsWebViewSignInHoster() returned true (the set lookup can't succeed on null).
            CloseDeferred(new FileHosterLoginDto
            {
                Id = _original.Id,
                FileHosterName = hoster!,
                Username = _derivedUsername,
                Password = string.Empty,
                // Null (not empty) when there's no key, so the DTO reads cleanly for session-cookie hosters
                // whose credential lives in SessionCookie below.
                ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey,
                AccountType = _original.AccountType,
                Disabled = EnabledCheck.IsChecked != true,
                // Persist storage usage captured at Sign-in (or carried from the existing account). The
                // post-save re-verify can't re-read it for HitFile, and ApplySessionCookieIfPresent only
                // overwrites on non-null, so this is the DTO's only chance to hold a usage figure.
                StorageUsedBytes = _storageUsedBytes,
                StorageQuotaBytes = _storageQuotaBytes,
                // Persist the captured login session — with its expiry + proxy pin — so "Check / Refresh" and
                // the web-form upload path can reuse it without re-opening the WebView.
                SessionCookie = _sessionCookie,
                SessionCookieExpiresUtc = _sessionCookieExpiresUtc,
                PinnedProxyId = _pinnedProxyId,
                CreatedDateTime = _createdDateTime,
            });
            return;
        }

        // Classic username/password hoster.
        string username = UsernameBox.Text?.Trim() ?? string.Empty;
        string password = PasswordBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            await MessageBoxWindow.ShowWarningAsync(
                this,
                Localizer.Instance["EditAccount_Validation_RequireUsernameAndPassword"],
                Localizer.Instance["Common_Error"]);
            (string.IsNullOrWhiteSpace(username) ? UsernameBox : PasswordBox).Focus();
            return;
        }

        FileHosterLoginDto edited = new()
        {
            Id = _original.Id,
            FileHosterName = hoster!,
            Username = username,
            Password = password,
            // NOT null: a classic username/password hoster can still have a DERIVED key as its actual
            // upload credential (FileMirage's api_token, Pixeldrain's auth_key). Blanking it here leaves
            // an account that looks correct in the grid and cannot upload.
            ApiKey = string.IsNullOrEmpty(ApiKeyBox.Text) ? _original.ApiKey : ApiKeyBox.Text,
            AccountType = _original.AccountType,
            Disabled = EnabledCheck.IsChecked != true,
            // Preserve previously-captured usage + session across an edit — SaveEditedAccountAsync persists
            // this DTO verbatim (no re-verify), so omitting them would blank the grid's Used/Available cells
            // (and drop the session) until the next manual refresh.
            StorageUsedBytes = _storageUsedBytes,
            StorageQuotaBytes = _storageQuotaBytes,
            SessionCookie = _sessionCookie,
            SessionCookieExpiresUtc = _sessionCookieExpiresUtc,
            PinnedProxyId = _pinnedProxyId,
            CreatedDateTime = _createdDateTime,
        };

        if (_validateAccount is null)
        {
            CloseDeferred(edited);
            return;
        }

        await CheckThenCloseAsync(edited);
    }

    /// <summary>
    /// Proves the credentials with the host before the dialog closes, so a rejected password is
    /// corrected here rather than costing the user everything they typed.
    /// <para>
    /// Save is disabled and a status line appears for the duration; Cancel means "stop checking"
    /// while one is running. On success the verifier's result is stamped onto the account — for
    /// several hosters that check is the only place the upload credential ever exists — and the
    /// dialog closes with it. On failure the message is shown over this window and the dialog stays
    /// open with the fields as they were.
    /// </para>
    /// </summary>
    private async Task CheckThenCloseAsync(FileHosterLoginDto edited)
    {
        using CancellationTokenSource cts = new();
        _checkCts = cts;
        SaveButton.IsEnabled = false;
        CheckingStatus.IsVisible = true;

        AccountCheckResult? result = null;
        string? failure = null;
        try
        {
            result = await _validateAccount!(edited, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // The user pressed Cancel mid-check: no message, just hand the dialog back.
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            _checkCts = null;
            CheckingStatus.IsVisible = false;
            SaveButton.IsEnabled = true;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        if (result is { IsValid: true })
        {
            edited.AccountType = result.AccountType;
            AccountCheckOutcome.Apply(edited, result);
            edited.MarkRefreshed(AccountCheckStatus.Valid, result.Message ?? string.Empty, DateTime.Now);
            CloseDeferred(edited);
            return;
        }

        await MessageBoxWindow.ShowErrorAsync(
            this,
            failure ?? result?.Message ?? Localizer.Instance["EditAccount_SignIn_FailedGeneric"],
            Localizer.Instance["Common_Error"]);

        // Deliberately still open. The user dismisses the error and edits the password in place.
        PasswordBox.Focus();
    }

    // Cancel/Esc → null, EXCEPT while a check is running, where it means "stop checking" and leaves
    // the dialog open — the user asked to cancel the wait, not to throw away what they typed.
    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_checkCts is { } running)
        {
            running.Cancel();
            return;
        }

        CloseDeferred(null);
    }

    /// <summary>
    /// Closes on the next dispatcher pass rather than inline.
    /// <para>
    /// A button raises <c>Click</c> from <b>KeyDown</b> when it's activated from the keyboard, so closing
    /// straight from the handler destroys the window halfway through an input sequence — and the matching
    /// <b>KeyUp</b> is then delivered to a window whose <c>PlatformImpl</c> is already gone. Windows
    /// tolerates that and logs <c>[Control] PlatformImpl is null, couldn't handle input</c>; the headless
    /// platform takes the whole process down, which is how the regression test pins it. Letting the current
    /// event finish routing first costs one dispatcher frame and makes both stop.
    /// </para>
    /// </summary>
    private void CloseDeferred(FileHosterLoginDto? result)
        => Dispatcher.UIThread.Post(() => Close(result));
}
