// <copyright file="HosterCredentialModes.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace CSUploader.Upload;

/// <summary>How an account's credentials are entered/held for a hoster — drives the
/// EditAccountWindow credential UI on both heads. Hoisted from the WPF EditAccountWindow
/// code-behind (Phase 5 prep item 4) so a new hoster wired on master cannot silently miss
/// the Avalonia editor's copy.</summary>
public enum HosterCredentialMode
{
    /// <summary>Classic username + password entry.</summary>
    UsernamePassword,

    /// <summary>WebView sign-in that derives an API key, or a manually pasted key.</summary>
    ApiKey,

    /// <summary>WebView sign-in whose ONLY credential is the captured session cookie.</summary>
    SessionCookie,
}

/// <summary>
/// Single source of truth for which credential UI a hoster gets in EditAccountWindow. Hoisted
/// from the WPF <c>EditAccountWindow</c> code-behind (Phase 5 prep item 4) so the two heads
/// cannot drift when a new hoster is wired — both editors and both test suites read these sets.
/// </summary>
public static class HosterCredentialModes
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
    // "DropGalaxy" DISABLED 2026-07-26, the day it was added — anonymous uploads are capped at
    // 0.00001 MB (~10 bytes) and registration is closed, so the API-key path is unreachable too.
    // Removed here alongside its registry + DI entries; see DropGalaxyPipeline.cs for the diagnosis.
    private static readonly HashSet<string> ApiKeyHosters =
        [with(StringComparer.OrdinalIgnoreCase), "Buzzheavier", "Ex-Load", "KatFile", "Hexload", "Hxfile", "FileBoom", "HitFile", "Turbobit", "Keep2Share", "TezFiles", "NitroFlare", "Ufile", "Send.now", "Uploadrar"];

    /// <summary>
    /// WebView-sign-in hosters whose ONLY credential is the captured session cookie — there is no
    /// API key to paste. The dialog shows them the same Sign-in button as <see cref="ApiKeyHosters"/>
    /// but HIDES the "OR paste an API key" box, and keys sign-in success / Save on the captured cookie
    /// instead of an API key. Both members are classic XFileSharing hosts running the pipeline's
    /// web-form path: isra.cloud exposes no REST API at all, and uploady.io mints an API key only on
    /// request (its my_account reports "No API Key Found"), so neither has a key to paste.
    /// </summary>
    // "DDownload" is here rather than in ApiKeyHosters on purpose: it HAS a working REST API, but the
    // key is only obtainable from its Affiliate Dashboard (Affiliate → Settings) and can't be
    // bootstrapped from my_account, so requiring one would mean every user enabling an affiliate
    // account before their first upload. Signing in is the shippable flow.
    // "Filestank" is the same story on a different platform — YetiShare, not XFileSharing. Its
    // /api/v2 wants two 64-character keys and its account area exposes no page that yields them, so
    // the credential is the filehosting session cookie captured by the WebView.
    // "Filedot" likewise: its REST API answers the family's "Invalid key", but My Account, My Files,
    // Reports and Earn Money were all walked and none of them ever prints a key.
    private static readonly HashSet<string> SessionCookieHosters =
        [with(StringComparer.OrdinalIgnoreCase), "Isracloud", "Uploady", "Clicknupload", "DDownload", "Filestank", "Filedot"];

    /// <summary>Classifies a hoster into its <see cref="HosterCredentialMode"/>. Null / unknown
    /// hosters fall back to classic <see cref="HosterCredentialMode.UsernamePassword"/>.</summary>
    public static HosterCredentialMode GetMode(string? hosterName) =>
        hosterName is null ? HosterCredentialMode.UsernamePassword
        : ApiKeyHosters.Contains(hosterName) ? HosterCredentialMode.ApiKey
        : SessionCookieHosters.Contains(hosterName) ? HosterCredentialMode.SessionCookie
        : HosterCredentialMode.UsernamePassword;

    /// <summary>WebView-sign-in hoster that derives (or accepts a pasted) API key — see
    /// <see cref="ApiKeyHosters"/>.</summary>
    public static bool IsApiKeyHoster(string? hosterName) => GetMode(hosterName) == HosterCredentialMode.ApiKey;

    /// <summary>WebView-sign-in hoster whose only credential is the session cookie (no pasteable
    /// API key) — see <see cref="SessionCookieHosters"/>.</summary>
    public static bool IsSessionCookieHoster(string? hosterName) => GetMode(hosterName) == HosterCredentialMode.SessionCookie;

    /// <summary>Either WebView-sign-in family (API-key or session-cookie): both hide username/password
    /// and surface the Sign-in button.</summary>
    public static bool IsWebViewSignInHoster(string? hosterName) => GetMode(hosterName) != HosterCredentialMode.UsernamePassword;
}
