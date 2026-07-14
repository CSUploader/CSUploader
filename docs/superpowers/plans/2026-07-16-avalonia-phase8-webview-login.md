# Avalonia Migration Phase 8: WebView Login — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Avalonia head a working interactive (captcha-gated, WebView2-hosted) sign-in: port the WPF `WebViewLoginWindow` logic onto the Phase 2-spiked `NativeControlHost`/`CoreWebView2Controller` host, ship the real `AvaloniaWebViewInteractiveAuthService` (the head's `IInteractiveAuthService`), wire it into DI in place of the throwing stub, and retire the spike — so every captcha-gated hoster (ex-load, HitFile, FileBoom/Keep2Share, isra, TakeFile, …) can sign in on the Avalonia head exactly as it does on the WPF head.

**Architecture:** Strangler step 8 (`docs/superpowers/specs/2026-07-10-avalonia-migration-design.md`, section Phases "Phase 8" + its ADAPTATION ADDITION, the §"WebView2 login" adaptation list at design line 79, and the §"WebView2 limitation" verification note at line 88). The seam is NARROW: the interactive-login callback (`SettingsViewModel.InteractiveLoginAsync` → `AccountVerifier` → the hoster pipeline → `IInteractiveAuthService.AcquireSessionCookieAsync`) already lives in Core and is wired on BOTH heads; `EditAccountWindow`'s `Func<string,Task<AccountCheckResult>>? interactiveLogin` is already non-null in production (`src/CSUploader.Avalonia/Services/AvaloniaDialogService.cs:209,220` ← `src/CSUploader.Core/ViewModels/SettingsViewModel.cs:740,765,1205`). The ONLY stubbed piece is `IInteractiveAuthService` itself — registered as `StubInteractiveAuthService` (throws `NotSupportedException`) at `src/CSUploader.Avalonia/App.axaml.cs:256`. This phase supplies the Avalonia login window + auth service, flips that one DI line, and deletes the spike. **Core is untouched this phase.**

**Tech Stack:** unchanged — .NET 10, Avalonia 11.3.18 + Avalonia.Controls.DataGrid 11.3.13 + Avalonia.Themes.Fluent + Avalonia.Svg.Skia 11.3.0, `Microsoft.Web.WebView2` 1.0.4022.49 (Core wrapper only; the `_DropWebView2DesktopWrappers` target strips the WPF/WinForms wrappers), Avalonia.Headless.XUnit 11.3.18, CommunityToolkit.Mvvm 8.4.2 (Core), Moq. This phase adds NO packages (all WebView2 plumbing shipped in Phase 2). Bridge via `scripts/ava-drive.cs`; contact sheet via `scripts/contact-sheet.py`.

## Global Constraints

- Repo worktree: `E:\Projects\CSUploader\CSUploader-avalonia`, branch `avalonia-migration`, starting from tag `phase7-shell-ready` (tip `52c7400`). **NEVER touch `E:\Projects\CSUploader\CSUploader`** (the maintainer's main tree — has uncommitted Buzzheavier work). HARNESS: PowerShell's default cwd is the MAIN tree — ALWAYS use absolute worktree paths for build/test; use PowerShell (not Bash) for `-p:OutDir=D:\...` builds (Bash strips the backslashes → a bridge drive launches a STALE exe).
- Suite gate after every task (definition of done):
  - `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests` — **1201 green** at phase start (Phase 7 verdict); a regression net only — this phase adds NO shared/WPF tests, so the count stays 1201.
  - `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests` — **418 green** at phase start (Phase 7 verdict). Confirm the exact number at Task 1's gate and correct it here if it drifted; every task that adds tests raises it — record each new baseline and carry it forward.
  - Separate OutDirs are mandatory (a shared OutDir mixes WPF and Avalonia assemblies and breaks discovery). Never run bare solution-level `dotnet test -p:OutDir=...`.
- Head builds: Avalonia `dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\ava`; WPF `dotnet build src/CSUploader.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\wpf`. Scratch DBs live beside those exes; seed with `dotnet run scripts/seed-fake-data.cs -- <outdir>` (idempotent, synthesized bogus data only).
- Every csproj keeps `LangVersion=preview`, `Nullable=enable`, `ImplicitUsings=enable`, TFM `net10.0-windows10.0.17763.0`, `EnableWindowsTargeting=true`. Version pins are hard; do not bump anything.
- **Core is UNTOUCHED this phase.** The gate (Task 8) asserts `git diff phase7-shell-ready..HEAD -- src/CSUploader.Core/` is EMPTY. If a port seems to need a Core change (e.g. an `IUiDispatcher.InvokeAsync(Func<Task>)` overload), STOP and surface it to the team lead as a DECISION — do not just add it. (The plan is designed to need none — see Task 6's TCS-bridge marshal.)
- **The WPF head is UNTOUCHED this phase.** No reference-shot driver is added (the WebView content is an un-screenshottable native HWND — design line 88 — so a WPF login-chrome reference shot has no comparison value and would force a live-WebView2 shot harness). The gate asserts `git diff phase7-shell-ready..HEAD -- src/` outside `src/CSUploader.Avalonia/**` is EMPTY. All Phase 8 work lands under `src/CSUploader.Avalonia/**` (+ this plan doc + the design reconcile in Task 8).
- i18n: NO new keys this phase. Every string the port needs already exists and is LIVE in the WPF head (verified in `src/CSUploader.Core/Resources/Strings.resx`): `WebViewLogin_WindowTitle`, `WebViewLogin_Header_Format`, `WebViewLogin_Instructions`, `WebViewLogin_Status_Initializing`, `WebViewLogin_Status_Loading_Format`, `WebViewLogin_Status_CookieReadFailed_Format`, `WebViewLogin_Error_InitFailed_Format`, `WebViewLogin_Error_UnsupportedProxy_Title`, `WebViewLogin_Error_SocksAuthUnsupported_Format`, `Common_Cancel`, `Common_Error`. The phase-gate diff must show zero `Strings*.resx` changes. Never hand-edit resx.
- **Agent-safety (unchanged, and load-bearing for this phase):** NO task may drive a real hoster login. The agent cannot verify native typing/focus (Windows refuses a background agent the foreground grab keystroke delivery needs — Phase 2 spike verdict §"Why (a)…"), so real sign-ins + the Turnstile challenge + 125%/150% DPI are the maintainer's manual cutover step. Agent-verifiable surface (design line 88): the login window OPENS over an owner, its navigation lifecycle surfaces on a bridge-readable login VM (ava_vm), and it CLOSES cleanly — all driven against a benign local URL (`about:blank`), Direct proxy, no credentials. Avalonia bridge launches always pass `--agent`; scratch DBs only; never copy a real `CSUploader.db`.
- `[AvaloniaFact]` discipline (Phase 3 rule): tests that open windows or mutate a process-global static restore that state in `finally`; close every window opened (snapshot the window list before closing). Pure-logic tests use plain `[Fact]`.
- Shots convention (extends Phase 7): `D:\temp2\cbuild-mig\shots\<view>-<light|dark>-<wpf|ava>.png`. Phase 8's one contact-sheet view is `webview-login` (the Avalonia login-window CHROME — header, status strip, Cancel — captured via the gallery demo; the WebView area is a native HWND and shows blank/host chrome, documented per design line 88). There is NO `-wpf` cell (WPF head untouched; chrome parity is verified by reading the WPF XAML `src/Views/WebViewLoginWindow.xaml`).
- Defender-ML false-positive (Phase 5/6 finding): the demo/gallery/test code carries NO dense hoster-URL literals — use `about:blank` / `https://example.test/` placeholders only.
- Commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- When a task says "mirror the WPF site", open the cited `file:line` and copy the semantics exactly. WPF originals: `src/Views/WebViewLoginWindow.xaml` + `.xaml.cs`, `src/Services/WebViewInteractiveAuthService.cs`. The working Avalonia reference (the Phase 2 spike, THROWAWAY, deleted in Task 7): `src/CSUploader.Avalonia/Spike/WebView2HwndHost.cs` + `WebView2SpikeWindow.axaml(.cs)`.

### Scope coverage (design Phase 8 line + ADAPTATION ADDITION)

| # | Requirement (design line 103 + line 79 adaptation list) | Task |
|---|----------------------------------------------------------|------|
| 1 | Port `WebViewLoginWindow` logic onto the spiked host (`CreateCoreWebView2ControllerAsync(hwnd)`, per-hoster user-data folder, bounds sync, proxy/cert, cookie/probe capture) | Tasks 3 + 4 |
| 2 | `DispatcherTimer → Avalonia DispatcherTimer` (the completion poll) | Task 4 |
| 3 | `MessageBox on init-failure → custom message box` (`MessageBoxWindow`) | Task 3 |
| 4 | `WPF DialogResult flips + "throws once closing" race guard → Avalonia Close(result) completion plumbing (single-completion guard kept)` | Task 4 (rule 49) |
| 5 | `Loaded → Opened`/controller-ready; `EnsureCoreWebView2Async/WebView.Dispose → controller create/Close (user-data-folder lock release)` | Task 3 |
| 6 | Ship the real `WebViewInteractiveAuthService` (sheds its WPF dispatcher via `IUiDispatcher`); swap DI off the stub | Task 6 |
| 7 | **ADAPTATION ADDITION** — focus integration the spike never exercised: `CoreWebView2Controller.MoveFocusRequested` (Tab-out of the page), focus-on-activation (window Activated → `controller.MoveFocus`), initial focus | Task 5 |
| 8 | Delete/retire the spike; the `--agent`+bridge `EnableMutations` coupling is already mechanical (`App.axaml.cs:229`) — verify only | Task 7 |
| 9 | Agent verifies window open + navigation events (login VM) + open/close mechanics; unit tests for cookie/probe logic | Tasks 2/3 (VM + gallery demo) + Task 1 (pure capture tests) |

### GO/NO-GO judgment (why there is NO probe task)

The Phase 2 spike (verdict `docs/superpowers/specs/2026-07-11-webview2-spike-verdict.md`) already returned **GO** on the sign-in architecture: child-HWND hosting, controller creation on `CreateCoreWebView2ControllerAsync(hwnd)`, bounds = host DIP × `RenderScaling` synced on layout+move+`ScalingChanged`, `ShowDialog` modal ownership, `Controller.Close()` releasing the user-data-folder lock, and `CookieManager` HttpOnly reads all passed. The abort criterion (fall back to a WPF login-helper exe) is **not** triggered. The one genuinely-new piece — focus integration (item 7) — is **NOT a GO/NO-GO gate**: (a) the login still works if focus is imperfect (the user clicks fields with the mouse); (b) its runtime behavior (native Tab-out/typing) is **agent-unverifiable** by construction (foreground-grab refusal), so a "probe task" the agent runs could not validate it anyway. It is therefore implemented as a normal task (Task 5) with review + a maintainer-verified gate-surface item, NOT a probe. Consequently this plan has **no Task-1 probe**; Task 1 is the pure helper foundation. (The WebView2 focus API *surface* — `MoveFocus`, `MoveFocusRequested`, `CoreWebView2MoveFocusReason` — was pinned against the installed SDK XML during planning; see the Reality-check register.)

## Port rules (new this phase)

Rules 1-17 (Phase 4), 18-32 (Phase 5), 33-40 (Phase 6), 41-48 (Phase 7) are carried forward by reference. This phase adds one row.

| # | WPF | Avalonia |
|---|-----|----------|
| 49 | Modal completion: set `Captured*` properties then flip `Window.DialogResult = true`; a second flip after the window starts closing THROWS, so a `_completed` bool guards the poll-vs-navigation race; the caller (`WebViewInteractiveAuthService`) reads the `Captured*` properties AFTER `ShowDialog()` and assembles `InteractiveAuthResult` (`WebViewInteractiveAuthService.cs:121-138`; the window's `Captured*` property decls are `WebViewLoginWindow.xaml.cs:117-146`) | The WINDOW assembles `InteractiveAuthResult` itself and completes via `Close(result)` (`ShowDialog<InteractiveAuthResult?>`); Cancel/Esc/X → `Close(null)` (which `ShowDialog<InteractiveAuthResult?>` returns as `default` = `null`). The single-completion `_completed` guard is KEPT (only the first poll/nav caller stops the timer + `Close`s). The auth service just `await`s the `ShowDialog<InteractiveAuthResult?>` value — no post-close property reads. Avalonia `Button.IsCancel` routes Esc through `Click` but does NOT auto-close (port rule 7), so the Cancel handler `Close(null)`s explicitly |

---

## Task 1: Pure capture + proxy helpers (headless, TDD)

The correctness-critical decisions from the WPF window/service — which cookie is the session cookie (a wrong pick = a silent anonymous session, per the ex-load/Hxfile findings), the JSON-string unwrap of a probe result, the `Cookie:` header build, the `--proxy-server` arg, the per-hoster folder name, and the SOCKS-with-auth refusal — extracted as pure static functions the window (Tasks 3/4) and the service (Task 6) call, so they are unit-tested WITHOUT a live WebView. `CoreWebView2Cookie` has no public constructor, so the cookie functions take `(string Name, string Value)` tuples; the window projects the live cookies to tuples at the call site.

**Files:**
- Create: `src/CSUploader.Avalonia/Views/WebViewLoginCapture.cs`
- Create: `src/CSUploader.Avalonia/Views/WebViewLoginProxy.cs`
- Test: `tests/CSUploader.Avalonia.Tests/Views/WebViewLoginCaptureTests.cs`
- Test: `tests/CSUploader.Avalonia.Tests/Views/WebViewLoginProxyTests.cs`

**Interfaces:**
- Produces: `CSUploader.Views.WebViewLoginCapture` (static) —
  - `static CookieSelection SelectCookies(IEnumerable<(string Name, string Value)> cookies, string cookieName, string? usernameCookieName, IReadOnlyList<string>? additionalCookieNames, Func<string, bool>? cookieValueValidator)`
  - `static string? TryParseJsonString(string? raw)`
  - `static string? BuildCookieHeader(IEnumerable<(string Name, string Value)> cookies)`
  - `readonly record struct CookieSelection(string? SessionValue, string? UsernameValue, IReadOnlyDictionary<string, string>? AdditionalCookies)` — `SessionValue` non-null ⇔ a valid session cookie matched (and passed the validator).
- Produces: `CSUploader.Views.WebViewLoginProxy` (static) —
  - `static string? BuildProxyServerArg(ProxyChoice? proxy)`
  - `static string SanitizeFolderName(string name)`
  - `static ProxyResolution ResolveProxyCredentials(ProxyChoice proxy)`
  - `readonly record struct ProxyResolution(ProxyCredentials? Credentials, bool SocksAuthUnsupported)`
  - `public sealed record ProxyCredentials(string Username, string? Password)`
- Consumes: `CSUploader.Lib.Net.ProxyChoice` (Core: `record ProxyChoice(int Id, IWebProxy? WebProxy, string Description)`).

- [x] **Step 1: Write the failing capture tests.** Create `tests/CSUploader.Avalonia.Tests/Views/WebViewLoginCaptureTests.cs`:

```csharp
// <copyright file="WebViewLoginCaptureTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Pure cookie/probe selection logic extracted from the WPF WebViewLoginWindow (Phase 8 Task 1). A wrong
/// session-cookie pick would hand the pipeline a stale/anonymous session (the ex-load / Hxfile findings), so
/// this is the correctness-critical half — and the only half unit-testable without a live WebView2.
/// </summary>
public class WebViewLoginCaptureTests
{
    [Fact]
    public void SelectCookies_PicksNamedSession_IgnoresEmptyValues()
    {
        var result = WebViewLoginCapture.SelectCookies(
            [("other", "x"), ("xfss", ""), ("xfss", "SESSION")], // first xfss empty → skipped
            cookieName: "xfss", usernameCookieName: null, additionalCookieNames: null, cookieValueValidator: null);

        Assert.Equal("SESSION", result.SessionValue);
        Assert.Null(result.UsernameValue);
        Assert.Null(result.AdditionalCookies);
    }

    [Fact]
    public void SelectCookies_NoMatch_ReturnsNullSession()
    {
        var result = WebViewLoginCapture.SelectCookies(
            [("a", "1"), ("b", "2")], cookieName: "xfss", usernameCookieName: null,
            additionalCookieNames: null, cookieValueValidator: null);

        Assert.Null(result.SessionValue);
    }

    [Fact]
    public void SelectCookies_ValidatorRejects_ReturnsNullSession()
    {
        // FileBoom-shape: the cookie is present pre-login too; the validator gates the post-login value.
        var result = WebViewLoginCapture.SelectCookies(
            [("accessToken", "bootstrap")], cookieName: "accessToken", usernameCookieName: null,
            additionalCookieNames: null, cookieValueValidator: v => v == "real");

        Assert.Null(result.SessionValue);
    }

    [Fact]
    public void SelectCookies_ValidatorAccepts_ReturnsValue()
    {
        var result = WebViewLoginCapture.SelectCookies(
            [("accessToken", "real")], cookieName: "accessToken", usernameCookieName: null,
            additionalCookieNames: null, cookieValueValidator: v => v == "real");

        Assert.Equal("real", result.SessionValue);
    }

    [Fact]
    public void SelectCookies_CapturesUsernameAndAdditional()
    {
        var result = WebViewLoginCapture.SelectCookies(
            [("xfss", "S"), ("username", "me@x.com"), ("pcId", "P1"), ("noise", "n")],
            cookieName: "xfss", usernameCookieName: "username", additionalCookieNames: ["pcId"],
            cookieValueValidator: null);

        Assert.Equal("S", result.SessionValue);
        Assert.Equal("me@x.com", result.UsernameValue);
        Assert.NotNull(result.AdditionalCookies);
        Assert.Equal("P1", result.AdditionalCookies!["pcId"]);
        Assert.False(result.AdditionalCookies.ContainsKey("noise"));
    }

    [Fact]
    public void TryParseJsonString_UnwrapsQuotedString()
        => Assert.Equal("42id", WebViewLoginCapture.TryParseJsonString("\"42id\""));

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("{not a string}")]
    public void TryParseJsonString_ReturnsNullForNonString(string? raw)
        => Assert.Null(WebViewLoginCapture.TryParseJsonString(raw));

    [Fact]
    public void BuildCookieHeader_JoinsNonEmptyPairs()
        => Assert.Equal("a=1; b=2", WebViewLoginCapture.BuildCookieHeader([("a", "1"), ("skip", ""), ("b", "2")]));

    [Fact]
    public void BuildCookieHeader_EmptyJar_ReturnsNull()
        => Assert.Null(WebViewLoginCapture.BuildCookieHeader([]));
}
```

- [x] **Step 2: Run it to verify it fails.** PowerShell: `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests`. Expected: compile failure (`WebViewLoginCapture` does not exist).

- [x] **Step 3: Implement `WebViewLoginCapture`.** Create `src/CSUploader.Avalonia/Views/WebViewLoginCapture.cs`:

```csharp
// <copyright file="WebViewLoginCapture.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Text.Json;

namespace CSUploader.Views;

/// <summary>
/// Pure cookie/probe selection logic for <see cref="WebViewLoginWindow"/>, extracted from the WPF window's
/// TryCaptureCookiesAsync / TryProbeAsync / BuildCookieHeaderAsync so it is unit-testable without a live
/// WebView2 (whose <c>CoreWebView2Cookie</c> has no public constructor). The window projects its live
/// cookie jar to <c>(Name, Value)</c> tuples at the call site.
/// </summary>
internal static class WebViewLoginCapture
{
    /// <summary>
    /// Picks the session cookie (first non-empty value named <paramref name="cookieName"/> that passes
    /// <paramref name="cookieValueValidator"/>), plus the optional identity cookie and any supplementary
    /// cookies. A null <see cref="CookieSelection.SessionValue"/> means "not signed in yet" — the caller
    /// keeps polling. Mirrors WebViewLoginWindow.xaml.cs:427-489.
    /// </summary>
    public static CookieSelection SelectCookies(
        IEnumerable<(string Name, string Value)> cookies,
        string cookieName,
        string? usernameCookieName,
        IReadOnlyList<string>? additionalCookieNames,
        Func<string, bool>? cookieValueValidator)
    {
        string? session = null;
        string? username = null;
        Dictionary<string, string>? additional = null;

        foreach ((string name, string value) in cookies)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (session is null && string.Equals(name, cookieName, StringComparison.Ordinal))
            {
                session = value;
            }
            else if (usernameCookieName is not null && username is null
                && string.Equals(name, usernameCookieName, StringComparison.Ordinal))
            {
                username = value;
            }
            else if (additionalCookieNames is not null)
            {
                foreach (string wanted in additionalCookieNames)
                {
                    if (string.Equals(name, wanted, StringComparison.Ordinal))
                    {
                        additional ??= new(StringComparer.Ordinal);
                        additional.TryAdd(name, value);
                        break;
                    }
                }
            }
        }

        // Validator opt-in (FileBoom's pre-login bootstrap JWT): reject a session value that doesn't pass,
        // so the window waits for the real post-login one.
        if (session is not null && cookieValueValidator is not null && !cookieValueValidator(session))
        {
            session = null;
        }

        return new CookieSelection(session, username, additional);
    }

    /// <summary>Decodes the JSON value <c>CoreWebView2.ExecuteScriptAsync</c> returns (e.g. <c>"\"id\""</c>)
    /// into a plain string. Returns null for <c>null</c>/non-string/invalid JSON. Mirrors WPF
    /// WebViewLoginWindow.xaml.cs:390-405.</summary>
    public static string? TryParseJsonString(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null")
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Joins a cookie jar into a single <c>name=value; name=value</c> header (empty pairs dropped);
    /// null when nothing to send. Mirrors WPF WebViewLoginWindow.xaml.cs:371-386.</summary>
    public static string? BuildCookieHeader(IEnumerable<(string Name, string Value)> cookies)
    {
        List<string> pairs = [];
        foreach ((string name, string value) in cookies)
        {
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))
            {
                pairs.Add(name + "=" + value);
            }
        }

        return pairs.Count > 0 ? string.Join("; ", pairs) : null;
    }
}

/// <summary>The outcome of <see cref="WebViewLoginCapture.SelectCookies"/>: the session value (null until a
/// valid one appears), the optional identity value, and any supplementary cookies (null when none asked
/// for / none present).</summary>
internal readonly record struct CookieSelection(
    string? SessionValue,
    string? UsernameValue,
    IReadOnlyDictionary<string, string>? AdditionalCookies);
```

- [x] **Step 4: Run the capture tests to verify they pass.** `dotnet test … -p:OutDir=D:\temp2\cbuild-mig\ava-tests`. Expected: the 12 new capture cases PASS (9 methods; the `TryParseJsonString` `[Theory]` contributes 4).

- [x] **Step 5: Write the failing proxy tests.** Create `tests/CSUploader.Avalonia.Tests/Views/WebViewLoginProxyTests.cs`:

```csharp
// <copyright file="WebViewLoginProxyTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using CSUploader.Lib.Net;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Pure proxy plumbing for the WebView sign-in (Phase 8 Task 1): the Chromium <c>--proxy-server</c> arg, the
/// per-hoster user-data folder name, and the SOCKS-with-auth refusal — ported from the WPF window/service so
/// the session's issuing IP matches the upload's IP (XFS binds session cookies to the issuing IP).
/// </summary>
public class WebViewLoginProxyTests
{
    [Fact]
    public void BuildProxyServerArg_Direct_ReturnsNull()
    {
        Assert.Null(WebViewLoginProxy.BuildProxyServerArg(ProxyChoice.Direct));
        Assert.Null(WebViewLoginProxy.BuildProxyServerArg(null));
    }

    [Fact]
    public void BuildProxyServerArg_UsesDescriptionVerbatim()
    {
        var proxy = new ProxyChoice(7, new WebProxy("https://p.example.test:8080"), "https://p.example.test:8080");
        Assert.Equal("https://p.example.test:8080", WebViewLoginProxy.BuildProxyServerArg(proxy));
    }

    [Fact]
    public void SanitizeFolderName_ReplacesInvalidChars()
    {
        // ':' and '/' are invalid on Windows → underscores; letters/digits survive.
        string s = WebViewLoginProxy.SanitizeFolderName("ex:load/1");
        Assert.DoesNotContain(':', s);
        Assert.DoesNotContain('/', s);
        Assert.Equal("ex_load_1".Length, s.Length);
    }

    [Fact]
    public void ResolveProxyCredentials_Direct_NoCreds_NoRefusal()
    {
        var r = WebViewLoginProxy.ResolveProxyCredentials(ProxyChoice.Direct);
        Assert.Null(r.Credentials);
        Assert.False(r.SocksAuthUnsupported);
    }

    [Fact]
    public void ResolveProxyCredentials_HttpsWithAuth_ReturnsCredentials()
    {
        var proxy = new ProxyChoice(3,
            new WebProxy("https://p:8080") { Credentials = new NetworkCredential("u", "pw") },
            "https://p:8080");
        var r = WebViewLoginProxy.ResolveProxyCredentials(proxy);
        Assert.NotNull(r.Credentials);
        Assert.Equal("u", r.Credentials!.Username);
        Assert.Equal("pw", r.Credentials.Password);
        Assert.False(r.SocksAuthUnsupported);
    }

    [Fact]
    public void ResolveProxyCredentials_SocksWithAuth_Refuses()
    {
        var proxy = new ProxyChoice(4,
            new WebProxy("socks5://p:1080") { Credentials = new NetworkCredential("u", "pw") },
            "socks5://p:1080");
        var r = WebViewLoginProxy.ResolveProxyCredentials(proxy);
        Assert.Null(r.Credentials);
        Assert.True(r.SocksAuthUnsupported);
    }

    [Fact]
    public void ResolveProxyCredentials_SocksNoAuth_NoRefusal()
    {
        var proxy = new ProxyChoice(5, new WebProxy("socks5://p:1080"), "socks5://p:1080");
        var r = WebViewLoginProxy.ResolveProxyCredentials(proxy);
        Assert.Null(r.Credentials);
        Assert.False(r.SocksAuthUnsupported); // no creds to satisfy → no refusal, just a direct-ish hop
    }
}
```

- [x] **Step 6: Run it to verify it fails.** Expected: compile failure (`WebViewLoginProxy` does not exist).

- [x] **Step 7: Implement `WebViewLoginProxy`.** Create `src/CSUploader.Avalonia/Views/WebViewLoginProxy.cs`:

```csharp
// <copyright file="WebViewLoginProxy.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using CSUploader.Lib.Net;

namespace CSUploader.Views;

/// <summary>
/// Pure proxy plumbing for <see cref="WebViewLoginWindow"/> / <see cref="Services.AvaloniaWebViewInteractiveAuthService"/>,
/// ported from the WPF window's BuildProxyServerArg/SanitizeFolderName (WebViewLoginWindow.xaml.cs:534-566) and
/// the WPF service's ResolveProxyCredentials (WebViewInteractiveAuthService.cs:148-187). The i18n of the SOCKS
/// refusal stays OUT of here (the service formats it) so this is Localizer-free and headlessly testable.
/// </summary>
internal static class WebViewLoginProxy
{
    /// <summary>Builds the Chromium <c>--proxy-server</c> value (<c>scheme://host:port</c>, no credentials —
    /// auth rides <c>BasicAuthenticationRequested</c>). Null for null/direct/no-WebProxy. ProxyChoice.Description
    /// is already scheme://host:port by construction in ProxyManager, which Chromium accepts verbatim.</summary>
    public static string? BuildProxyServerArg(ProxyChoice? proxy)
        => proxy is null || proxy.Id == 0 || proxy.WebProxy is null ? null : proxy.Description;

    /// <summary>Sanitises a hoster name into a directory segment (Windows-invalid chars → '_'). Mirrors the
    /// WPF SanitizeFolderName.</summary>
    public static string SanitizeFolderName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            buffer[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        }

        return new string(buffer);
    }

    /// <summary>
    /// Classifies the pinned proxy: returns HTTP/HTTPS Basic credentials for the 407 challenge, or flags
    /// SOCKS-with-auth (Chromium's <c>--proxy-server</c> can't carry SOCKS creds and there's no event to
    /// supply them — the service turns this flag into the localized refusal message). Direct / no-auth →
    /// neither.
    /// </summary>
    public static ProxyResolution ResolveProxyCredentials(ProxyChoice proxy)
    {
        if (proxy.Id == 0 || proxy.WebProxy is null)
        {
            return new ProxyResolution(null, false); // direct
        }

        bool isSocks = proxy.Description.StartsWith("socks", StringComparison.OrdinalIgnoreCase);

        NetworkCredential? cred = proxy.WebProxy.Credentials?.GetCredential(new Uri("https://example.com/"), "Basic");
        if (string.IsNullOrEmpty(cred?.UserName))
        {
            return new ProxyResolution(null, false); // no credentials to supply
        }

        return isSocks
            ? new ProxyResolution(null, true)
            : new ProxyResolution(new ProxyCredentials(cred!.UserName, cred.Password), false);
    }
}

/// <summary>Result of <see cref="WebViewLoginProxy.ResolveProxyCredentials"/>: the Basic credentials to feed
/// <c>BasicAuthenticationRequested</c> (null when none), and whether the proxy is the unsupported
/// SOCKS-with-auth shape.</summary>
internal readonly record struct ProxyResolution(ProxyCredentials? Credentials, bool SocksAuthUnsupported);

/// <summary>Username/password pair for HTTP/HTTPS proxy Basic auth in the embedded browser. Port of the WPF
/// <c>ProxyCredentials</c> record (WebViewLoginWindow.xaml.cs:574).</summary>
public sealed record ProxyCredentials(string Username, string? Password);
```

- [x] **Step 8: Run all Task 1 tests to verify they pass.** Expected: the 12 capture + 7 proxy cases PASS; the Avalonia suite total rises from 418 to ~437 (record the exact number). Build the Avalonia head too (`dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\ava`) — 0 warnings.

- [x] **Step 9: Commit.**

```
git add src/CSUploader.Avalonia/Views/WebViewLoginCapture.cs src/CSUploader.Avalonia/Views/WebViewLoginProxy.cs tests/CSUploader.Avalonia.Tests/Views/WebViewLoginCaptureTests.cs tests/CSUploader.Avalonia.Tests/Views/WebViewLoginProxyTests.cs
git commit -m "feat(avalonia): Phase 8 Task 1 - pure WebView login capture + proxy helpers

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 2: WebView login navigation-state VM (bridge surface, TDD)

The design's agent-verification (line 88) requires the navigation lifecycle "exposed on the login VM so ava_vm reads them." The WPF window is pure code-behind with a `StatusText` label; the Avalonia window keeps its logic in code-behind (per the per-window pattern) but sets its display + navigation lifecycle on a small observable VM used as the window's `DataContext`, so ava_vm confirms: window opened → initialized → navigated → completed. No commands (Cancel stays a code-behind `Click`); the VM is a display/state mirror only.

**Files:**
- Create: `src/CSUploader.Avalonia/Views/WebViewLoginViewModel.cs`
- Test: `tests/CSUploader.Avalonia.Tests/Views/WebViewLoginViewModelTests.cs`

**Interfaces:**
- Produces: `CSUploader.Views.WebViewLoginViewModel : System.ComponentModel.INotifyPropertyChanged` — observable get/set `string Header`, `string Status`, `bool IsInitialized`, `string? LastNavigationUrl`, `int NavigationCompletedCount`, `bool IsCompleted`; plus `void RecordNavigationCompleted(string? url)` (increments the count + sets `LastNavigationUrl`). Consumed by `WebViewLoginWindow` (Task 3) as its `DataContext`.

- [x] **Step 1: Write the failing VM tests.** Create `tests/CSUploader.Avalonia.Tests/Views/WebViewLoginViewModelTests.cs`:

```csharp
// <copyright file="WebViewLoginViewModelTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// The bridge-readable navigation-state VM behind the Avalonia WebView login window (Phase 8 Task 2). ava_vm
/// reads these to confirm the window opened, initialized, navigated and completed — the only agent-verifiable
/// surface, since the WebView content is a native HWND (design line 88). Plain observable state, no commands.
/// </summary>
public class WebViewLoginViewModelTests
{
    [Fact]
    public void Status_Set_RaisesPropertyChanged()
    {
        var vm = new WebViewLoginViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Status = "Loading";

        Assert.Equal("Loading", vm.Status);
        Assert.Contains(nameof(WebViewLoginViewModel.Status), raised);
    }

    [Fact]
    public void RecordNavigationCompleted_IncrementsCountAndUrl()
    {
        var vm = new WebViewLoginViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RecordNavigationCompleted("https://example.test/1");
        vm.RecordNavigationCompleted("https://example.test/2");

        Assert.Equal(2, vm.NavigationCompletedCount);
        Assert.Equal("https://example.test/2", vm.LastNavigationUrl);
        Assert.Contains(nameof(WebViewLoginViewModel.NavigationCompletedCount), raised);
        Assert.Contains(nameof(WebViewLoginViewModel.LastNavigationUrl), raised);
    }

    [Fact]
    public void SamePropertyValue_DoesNotReRaise()
    {
        var vm = new WebViewLoginViewModel { IsInitialized = true };
        int count = 0;
        vm.PropertyChanged += (_, _) => count++;

        vm.IsInitialized = true; // unchanged

        Assert.Equal(0, count);
    }
}
```

- [x] **Step 2: Run it to verify it fails.** Expected: compile failure (`WebViewLoginViewModel` does not exist).

- [x] **Step 3: Implement the VM.** Create `src/CSUploader.Avalonia/Views/WebViewLoginViewModel.cs`:

```csharp
// <copyright file="WebViewLoginViewModel.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CSUploader.Views;

/// <summary>
/// Bridge-readable navigation-state mirror for <see cref="WebViewLoginWindow"/> (design line 88: navigation
/// events "exposed on the login VM so ava_vm reads them"). The window's code-behind sets these; the header /
/// status strip bind to <see cref="Header"/> / <see cref="Status"/>. No commands — Cancel is a code-behind
/// <c>Click</c>; the WebView completion touches the native controller and cannot be a VM command.
/// </summary>
public sealed class WebViewLoginViewModel : INotifyPropertyChanged
{
    private string _header = string.Empty;
    private string _status = string.Empty;
    private bool _isInitialized;
    private string? _lastNavigationUrl;
    private int _navigationCompletedCount;
    private bool _isCompleted;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Hoster header line ("Sign in to X"). Bound to the header TextBlock.</summary>
    public string Header { get => _header; set => Set(ref _header, value); }

    /// <summary>Current status-strip text (initializing / loading URL / current source / cookie-read error).</summary>
    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>True once the environment + controller are created and the first navigation is kicked off.</summary>
    public bool IsInitialized { get => _isInitialized; set => Set(ref _isInitialized, value); }

    /// <summary>Most recent navigated URL (SourceChanged / NavigationCompleted).</summary>
    public string? LastNavigationUrl { get => _lastNavigationUrl; set => Set(ref _lastNavigationUrl, value); }

    /// <summary>Count of NavigationCompleted events — ava_vm's "did navigation actually happen" signal.</summary>
    public int NavigationCompletedCount { get => _navigationCompletedCount; set => Set(ref _navigationCompletedCount, value); }

    /// <summary>True once a session cookie / probe value was captured (sign-in success).</summary>
    public bool IsCompleted { get => _isCompleted; set => Set(ref _isCompleted, value); }

    /// <summary>Bumps <see cref="NavigationCompletedCount"/> and records the URL in one call (window use).</summary>
    public void RecordNavigationCompleted(string? url)
    {
        NavigationCompletedCount++;
        LastNavigationUrl = url;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
```

- [x] **Step 4: Run the VM tests to verify they pass.** Expected: the 3 new facts PASS; record the new Avalonia total.

- [x] **Step 5: Commit.**

```
git add src/CSUploader.Avalonia/Views/WebViewLoginViewModel.cs tests/CSUploader.Avalonia.Tests/Views/WebViewLoginViewModelTests.cs
git commit -m "feat(avalonia): Phase 8 Task 2 - WebView login navigation-state VM (ava_vm surface)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 3: Host control + login window (shell, controller lifecycle, bounds, proxy/cert, teardown, Cancel)

Promote the spike's HWND host into a permanent `Views/WebView2Host.cs`, then build the real `WebViewLoginWindow` (XAML + code-behind) with everything EXCEPT the completion/capture (Task 4) and focus integration (Task 5): environment creation (per-hoster user-data folder, `--proxy-server`, UA override), controller create via `CreateCoreWebView2ControllerAsync(hwnd)`, bounds sync (DIP × `RenderScaling` on layout+move+`ScalingChanged` — the spike recipe), navigation → VM, `BasicAuthenticationRequested`, `ServerCertificateErrorDetected`, session-cookie clear-on-open (the Hxfile finding), teardown via `controller.Close()` (releases the user-data-folder lock), init-failure → `MessageBoxWindow`, and Cancel/X → `Close(null)`. Add a DEBUG-only gallery demo launcher so the bridge can open it against `about:blank`.

The window is `Window` completing via `ShowDialog<InteractiveAuthResult?>` — at the end of this task the only completion is `Close(null)` (Cancel/init-failure); Task 4 adds the success `Close(result)`.

**Files:**
- Create: `src/CSUploader.Avalonia/Views/WebView2Host.cs` (promoted from `Spike/WebView2HwndHost.cs`)
- Create: `src/CSUploader.Avalonia/Views/WebViewLoginWindow.axaml`
- Create: `src/CSUploader.Avalonia/Views/WebViewLoginWindow.axaml.cs`
- Modify: `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml` (add `WebViewLoginDemoButton`)
- Modify: `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml.cs` (wire the demo handler)
- Test: `tests/CSUploader.Avalonia.Tests/Views/WebViewLoginWindowTests.cs`

**Interfaces:**
- Produces: `CSUploader.Views.WebView2Host : NativeControlHost` — `IntPtr Hwnd { get; }`, `event Action<IntPtr>? HwndReady`, `event Action? HwndDestroying`, `bool TryGetChildClientSize(out int width, out int height)`.
- Produces: `CSUploader.Views.WebViewLoginWindow : Window` — ctor `(string hosterName, string loginUrl, string cookieDomain, string cookieName, string? usernameCookieName = null, ProxyChoice? proxy = null, ProxyCredentials? proxyCredentials = null, Func<string,bool>? cookieValueValidator = null, IReadOnlyList<string>? additionalCookieNames = null, string? successProbeScript = null, string? cookieCaptureUrl = null, string? userAgentOverride = null, bool allowInvalidCertificates = false)` — completes via `ShowDialog<InteractiveAuthResult?>(owner)`. Consumed by `AvaloniaWebViewInteractiveAuthService` (Task 6) and the gallery demo.
- Consumes: `WebViewLoginViewModel` (Task 2), `ProxyCredentials`/`WebViewLoginProxy` (Task 1), `MessageBoxWindow.ShowErrorAsync` (`src/CSUploader.Avalonia/Views/MessageBoxWindow.axaml.cs:90`), `CSUploader.Lib.Localization.Localizer`, `CSUploader.Services.InteractiveAuthResult` (Core), `Microsoft.Web.WebView2.Core`.

- [x] **Step 1: Promote the HWND host.** Create `src/CSUploader.Avalonia/Views/WebView2Host.cs` by copying `src/CSUploader.Avalonia/Spike/WebView2HwndHost.cs` with FOUR edits: (a) delete the first-line `// THROWAWAY …` banner and add the copyright header block; (b) rename the type `WebView2HwndHost` → `WebView2Host`; (c) change `namespace CSUploader.Spike;` → `namespace CSUploader.Views;`; (d) **rewrite the doc comments so NO `Spike` / `WebView2SpikeWindow` reference survives** — the verbatim spike docstring (`WebView2HwndHost.cs:13` `<see cref="WebView2SpikeWindow"/>`, plus "spike window" at :19-20 and :30-31) would BOTH fail Task 8's `grep "Spike" → zero` AND become a dangling `cref` → CS1574 (breaking the 0-warning gate) once Task 7 deletes `WebView2SpikeWindow`. Keep `internal sealed class`, both events, `TryGetChildClientSize`, and all P/Invokes exactly. (The spike file stays until Task 7; the two classes coexist in different namespaces.) The promoted docstrings become:

```csharp
/// <summary>
/// A <see cref="NativeControlHost"/> that owns a bare Win32 child HWND (window class "static") to serve as
/// the parent window for a WebView2 <c>CoreWebView2Controller</c>. This host only manages the HWND lifecycle
/// and surfaces it; <see cref="WebViewLoginWindow"/> creates the controller (parented to <see cref="Hwnd"/>)
/// and keeps its bounds synced.
/// </summary>
/// <remarks>
/// Avalonia repositions the returned child HWND to overlay this control in physical pixels, so the WebView2
/// controller only ever needs a (0,0,width,height) fill of the child's client area. The child's client size is
/// exposed via <see cref="TryGetChildClientSize"/> as the physical ground truth the login window cross-checks
/// against DIP x RenderScaling.
/// </remarks>
```

and the `HwndReady` event summary becomes `/// <summary>Raised on the UI thread once the child HWND exists, so the login window can create the WebView2 controller parented to it.</summary>` (the `HwndDestroying` summary has no spike mention — copy it as-is).

- [x] **Step 2: Write the login window XAML.** Create `src/CSUploader.Avalonia/Views/WebViewLoginWindow.axaml` (port of `src/Views/WebViewLoginWindow.xaml`; the `<wv2:WebView2>` control becomes `<v:WebView2Host x:Name="Host"/>`; header/status bind to the VM):

```xml
<!-- Copyright (c) CSUploader. All rights reserved. -->
<!-- Licensed under the MIT license. See LICENSE file in the project root for full license information. -->

<!-- Modal captcha-gated sign-in browser (port of WPF src/Views/WebViewLoginWindow.xaml). Hosts a WebView2
     CoreWebView2Controller in a native child HWND (WebView2Host) — the WebView2 CONTROL doesn't exist for
     Avalonia, so the window owns the controller/bounds/teardown itself (Phase 2 spike pattern). Header +
     status bind to WebViewLoginViewModel (the DataContext) so ava_vm can read the navigation lifecycle (the
     WebView content is a native HWND, invisible to bridge screenshots — design line 88). Completion: the
     window assembles InteractiveAuthResult and Close(result)s (rule 49); Cancel/Esc/X -> Close(null). -->
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:loc="clr-namespace:CSUploader.Lib.Localization"
        xmlns:v="clr-namespace:CSUploader.Views"
        x:Class="CSUploader.Views.WebViewLoginWindow"
        Title="{loc:Loc WebViewLogin_WindowTitle}"
        Width="900" Height="720"
        WindowStartupLocation="CenterOwner"
        Background="{DynamicResource SurfaceMutedBrush}"
        Icon="/Assets/icon.ico">

  <Grid RowDefinitions="Auto,*,Auto">

    <!-- Header: which hoster + what we're waiting for. -->
    <Border Grid.Row="0" Padding="14,10" Background="{DynamicResource SurfaceBrush}"
            BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,0,0,1">
      <StackPanel>
        <TextBlock Text="{Binding Header}"
                   FontSize="13" FontWeight="SemiBold"
                   Foreground="{DynamicResource TextPrimaryBrush}" />
        <TextBlock Text="{loc:Loc WebViewLogin_Instructions}"
                   FontSize="11"
                   Foreground="{DynamicResource TextSecondaryBrush}"
                   Margin="0,2,0,0"
                   TextWrapping="Wrap" />
      </StackPanel>
    </Border>

    <v:WebView2Host Grid.Row="1" x:Name="Host" />

    <!-- Status strip: cookie-discovery state (code-behind sets VM.Status on every navigation). -->
    <Border Grid.Row="2" Padding="14,6" Background="{DynamicResource SurfaceBrush}"
            BorderBrush="{DynamicResource BorderBrush}" BorderThickness="0,1,0,0">
      <DockPanel>
        <Button DockPanel.Dock="Right" x:Name="CancelButton"
                Content="{loc:Loc Common_Cancel}"
                Width="90" Height="26"
                IsCancel="True"
                Click="CancelButton_Click" />
        <TextBlock Text="{Binding Status}"
                   FontSize="11"
                   Foreground="{DynamicResource TextSecondaryBrush}"
                   VerticalAlignment="Center"
                   TextTrimming="CharacterEllipsis" />
      </DockPanel>
    </Border>
  </Grid>
</Window>
```

- [x] **Step 3: Write the login window code-behind (shell + lifecycle, Cancel-only completion).** Create `src/CSUploader.Avalonia/Views/WebViewLoginWindow.axaml.cs`:

```csharp
// <copyright file="WebViewLoginWindow.axaml.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Services; // InteractiveAuthResult
using Microsoft.Web.WebView2.Core;

namespace CSUploader.Views;

/// <summary>
/// Modal browser to capture a session cookie / probe value from a captcha-gated hoster (port of WPF
/// src/Views/WebViewLoginWindow.xaml.cs). Hosts a <see cref="CoreWebView2Controller"/> in a native child
/// HWND (<see cref="WebView2Host"/>) — there is no Avalonia WebView2 CONTROL, so this window owns the
/// controller, its bounds (DIP x RenderScaling, the Phase 2 spike recipe), and teardown (controller.Close()
/// releases the per-hoster user-data-folder lock). Completion is Task 4; focus integration is Task 5.
/// </summary>
public partial class WebViewLoginWindow : Window
{
    private readonly WebViewLoginViewModel _vm = new();
    private readonly string _hosterName;
    private readonly string _loginUrl;
    private readonly string _cookieName;
    private readonly string? _usernameCookieName;
    private readonly Func<string, bool>? _cookieValueValidator;
    private readonly IReadOnlyList<string>? _additionalCookieNames;
    private readonly string? _successProbeScript;
    private readonly string? _cookieCaptureUrl;
    private readonly string? _userAgentOverride;
    private readonly bool _allowInvalidCertificates;
    private readonly ProxyChoice? _proxy;
    private readonly ProxyCredentials? _proxyCredentials;

    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private bool _creating;
    private System.Drawing.Rectangle _lastBounds;

    // Parameterless ctor for the Avalonia XAML tooling / runtime loader (AVLN3001). The app always uses the
    // full overload; this default constructs a harmless empty-spec window that never signs anything in.
    public WebViewLoginWindow()
        : this("(preview)", "about:blank", string.Empty, "__never__")
    {
    }

    public WebViewLoginWindow(
        string hosterName,
        string loginUrl,
        string cookieDomain,
        string cookieName,
        string? usernameCookieName = null,
        ProxyChoice? proxy = null,
        ProxyCredentials? proxyCredentials = null,
        Func<string, bool>? cookieValueValidator = null,
        IReadOnlyList<string>? additionalCookieNames = null,
        string? successProbeScript = null,
        string? cookieCaptureUrl = null,
        string? userAgentOverride = null,
        bool allowInvalidCertificates = false)
    {
        _hosterName = hosterName;
        _loginUrl = loginUrl;
        _ = cookieDomain; // informational on the spec; the WebView reads cookies by origin (matches WPF)
        _cookieName = cookieName;
        _usernameCookieName = usernameCookieName;
        _proxy = proxy;
        _proxyCredentials = proxyCredentials;
        _cookieValueValidator = cookieValueValidator;
        _additionalCookieNames = additionalCookieNames;
        _successProbeScript = successProbeScript;
        _cookieCaptureUrl = cookieCaptureUrl;
        _userAgentOverride = userAgentOverride;
        _allowInvalidCertificates = allowInvalidCertificates;

        InitializeComponent();
        DataContext = _vm;

        _vm.Header = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Header_Format"], hosterName);
        _vm.Status = Localizer.Instance["WebViewLogin_Status_Initializing"];

        Host.HwndReady += OnHwndReady;
        Host.HwndDestroying += TeardownController;

        // Bounds sync: Phase 2 spike recipe. Layout changes + window moves + a pure DPI change (drag to a
        // differently-scaled monitor — the 125%/150% DPI test) which changes RenderScaling with NO layout pass.
        Host.LayoutUpdated += (_, _) => SyncBounds();
        PositionChanged += (_, _) =>
        {
            SyncBounds();
            _controller?.NotifyParentWindowPositionChanged();
        };
        ScalingChanged += (_, _) => SyncBounds();

        Closed += (_, _) => TeardownController();
    }

    // NOTE: InitializeComponent() is emitted by the Avalonia source generator (partial class + WebViewLoginWindow.axaml) —
    // do NOT hand-write it (that is CS0111). This matches EditAccountWindow / MessageBoxWindow.

    // ---- Controller lifecycle (mirrors WebViewLoginWindow.xaml.cs:148-266 + the spike's OnHwndReady) -------

    private async void OnHwndReady(IntPtr hwnd)
    {
        if (_creating || _controller is not null)
        {
            return;
        }

        _creating = true;
        try
        {
            // Per-hoster user-data folder — persists captcha-solver trust across runs so the user need not
            // re-solve hCaptcha every login; per-hoster so two hosters can't leak cookies into each other.
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CSUploader", "WebView2", WebViewLoginProxy.SanitizeFolderName(_hosterName));
            Directory.CreateDirectory(userDataFolder);

            CoreWebView2EnvironmentOptions options = new();
            string? proxyArg = WebViewLoginProxy.BuildProxyServerArg(_proxy);
            if (proxyArg is not null)
            {
                options.AdditionalBrowserArguments = $"--proxy-server=\"{proxyArg}\"";
            }

            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userDataFolder, options: options);

            _controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
            _core = _controller.CoreWebView2;

            // Pin the UA before any navigation when the spec asks (Cloudflare cf_clearance binds to the exact
            // solving UA — TakeFile).
            if (!string.IsNullOrEmpty(_userAgentOverride))
            {
                _core.Settings.UserAgent = _userAgentOverride;
            }

            _core.NavigationCompleted += CoreWebView2_NavigationCompleted;
            _core.SourceChanged += CoreWebView2_SourceChanged;

            if (_proxyCredentials is not null)
            {
                _core.BasicAuthenticationRequested += CoreWebView2_BasicAuthenticationRequested;
            }

            if (_allowInvalidCertificates)
            {
                _core.ServerCertificateErrorDetected += CoreWebView2_ServerCertificateErrorDetected;
            }

            // Drop any persisted *session* cookie before navigating (the Hxfile finding): a stale one would be
            // captured the instant the page loads and close the window before a fresh login, handing the
            // pipeline an anonymous session. Symmetric with the capture read. Safe for every hoster (a cleared
            // profile == a first-ever sign-in); FileBoom's pre-login JWT is rejected by its validator anyway.
            _core.CookieManager.DeleteCookies(_cookieName, _loginUrl);

            // In UA-override (cf_clearance) mode ALSO drop the supplementary cookies — cf_clearance is bound to
            // the solving UA; a value persisted under the native UA would be captured stale.
            if (!string.IsNullOrEmpty(_userAgentOverride) && _additionalCookieNames is not null)
            {
                foreach (string name in _additionalCookieNames)
                {
                    _core.CookieManager.DeleteCookies(name, _loginUrl);
                }
            }

            _lastBounds = default;
            SyncBounds();

            _vm.IsInitialized = true;
            _vm.Status = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Status_Loading_Format"], _loginUrl);
            _core.Navigate(_loginUrl);
        }
        catch (Exception ex)
        {
            // WebView2 runtime missing / corrupt user-data folder — fail loudly (custom message box, design's
            // "MessageBox on init-failure -> custom message box") then close with no result.
            await MessageBoxWindow.ShowErrorAsync(
                this,
                string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Error_InitFailed_Format"], ex.Message),
                Localizer.Instance["Common_Error"]);
            Close(null);
        }
        finally
        {
            _creating = false;
        }
    }

    private void TeardownController()
    {
        if (_core is not null)
        {
            _core.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            _core.SourceChanged -= CoreWebView2_SourceChanged;
            _core.BasicAuthenticationRequested -= CoreWebView2_BasicAuthenticationRequested;
            _core.ServerCertificateErrorDetected -= CoreWebView2_ServerCertificateErrorDetected;
        }

        try
        {
            _controller?.Close(); // releases the per-hoster user-data-folder lock (spike verify d)
        }
        catch
        {
            // Best-effort — the window is closing regardless.
        }

        _controller = null;
        _core = null;
    }

    // ---- Bounds sync (Phase 2 spike recipe: source of truth = host DIP x RenderScaling) --------------------

    private void SyncBounds()
    {
        if (_controller is null)
        {
            return;
        }

        double scaling = RenderScaling;
        int w = Math.Max(1, (int)Math.Round(Host.Bounds.Width * scaling));
        int h = Math.Max(1, (int)Math.Round(Host.Bounds.Height * scaling));

        System.Drawing.Rectangle bounds = new(0, 0, w, h);
        if (bounds != _lastBounds)
        {
            _controller.Bounds = bounds;
            _lastBounds = bounds;
        }
    }

    // ---- Navigation -> VM (completion/capture is Task 4) --------------------------------------------------

    private void CoreWebView2_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (!_vm.IsInitialized || _core is null)
        {
            return;
        }

        _vm.Status = _core.Source ?? string.Empty;
        _vm.LastNavigationUrl = _core.Source;
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        => _vm.RecordNavigationCompleted(_core?.Source);

    private void CoreWebView2_BasicAuthenticationRequested(object? sender, CoreWebView2BasicAuthenticationRequestedEventArgs e)
    {
        // Fires for 401 (origin) AND 407 (proxy). Feeding proxy creds on a 401 is harmless (the origin rejects
        // + re-prompts, visible in the WebView); the 407 case — the one we want — succeeds immediately.
        if (_proxyCredentials is null)
        {
            return;
        }

        e.Response.UserName = _proxyCredentials.Username;
        e.Response.Password = _proxyCredentials.Password ?? string.Empty;
    }

    private void CoreWebView2_ServerCertificateErrorDetected(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
        // AlwaysAllow == the C# handler's DangerousAcceptAnyServerCertificateValidator; only ever reached when
        // the user explicitly enabled AllowInvalidServerCertificates.
        => e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);
}
```

- [x] **Step 4: Add the DEBUG gallery demo launcher (bridge surface).** In `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml`, alongside the existing dialog-launcher buttons, add (find the dialog buttons block by the `DialogEditAccountClassicButton` name):

```xml
<Button x:Name="WebViewLoginDemoButton" Content="WebView login (demo)" />
```

In `src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml.cs`, inside the ctor where the other `DialogEditAccount*Button.Click += …` are wired, add:

```csharp
WebViewLoginDemoButton.Click += OnShowWebViewLoginDemo;
```

and add the handler (mirroring the other `On*` handlers; DEBUG-safe — `about:blank`, Direct proxy, no credentials):

```csharp
// Phase 8 demo: opens the REAL WebViewLoginWindow against about:blank (no network, no live hoster, no
// credentials) so the bridge can verify open/navigation-VM/close mechanics + capture the chrome for the
// contact sheet. The native WebView area is invisible to bridge screenshots (design line 88); ava_vm reads
// the navigation VM instead. Replaces the retired --webview-spike surface.
private void OnShowWebViewLoginDemo(object? sender, RoutedEventArgs e)
    => _ = new WebViewLoginWindow("Demo", "about:blank", string.Empty, "__never__",
        proxy: CSUploader.Lib.Net.ProxyChoice.Direct).ShowDialog<CSUploader.Services.InteractiveAuthResult?>(this);
```

(If `GalleryWindow` has no `using CSUploader.Views;`, add it; the `_ = …` discards the awaitable — the demo is fire-and-forget for the bridge.)

- [x] **Step 5: Write the failing window construction test.** Create `tests/CSUploader.Avalonia.Tests/Views/WebViewLoginWindowTests.cs`:

```csharp
// <copyright file="WebViewLoginWindowTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia.Headless.XUnit;
using CSUploader.Lib.Net;
using CSUploader.Views;

namespace CSUploader.Tests.Avalonia.Views;

/// <summary>
/// Headless construction of the Avalonia WebView login window (Phase 8 Task 3). Constructing WITHOUT showing
/// never attaches the NativeControlHost, so no child HWND / WebView2 is created — safe headlessly. The live
/// controller + real sign-in are the maintainer's manual cutover step (design line 88; agent can't grab foreground).
/// </summary>
public class WebViewLoginWindowTests
{
    [AvaloniaFact]
    public void Constructs_WithVmDataContext_AndFormattedHeader()
    {
        var window = new WebViewLoginWindow("ex-load", "about:blank", ".ex-load.com", "xfss",
            proxy: ProxyChoice.Direct);
        try
        {
            var vm = Assert.IsType<WebViewLoginViewModel>(window.DataContext);
            Assert.Contains("ex-load", vm.Header); // WebViewLogin_Header_Format applied
            Assert.False(vm.IsInitialized);         // no HwndReady until shown
            Assert.False(vm.IsCompleted);
            Assert.Equal(0, vm.NavigationCompletedCount);
        }
        finally
        {
            // The existing window tests Show() then Close() in a finally (e.g. UploadWizardSummaryTests.cs:44,77;
            // SettingsAccountsTests.cs:36,64). This test deliberately NEVER shows the window — showing attaches
            // the NativeControlHost -> HwndReady -> a real CoreWebView2 (native, Evergreen-runtime resources),
            // which must not happen headlessly. A never-shown window created no platform peer, so there is
            // nothing to close; the guard is a no-op here and documents that divergence.
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }
}
```

- [x] **Step 6: Run it to verify it fails, then passes after implementation.** `dotnet test … -p:OutDir=D:\temp2\cbuild-mig\ava-tests`. Expected before Steps 1-4 exist: compile failure; after: PASS. Build the Avalonia head (0 warnings).

- [x] **Step 7: Bridge-verify the demo (agent-safe).** PowerShell build to a scratch OutDir, seed, launch with `--agent --gallery`, click `WebViewLoginDemoButton` via `scripts/ava-drive.cs`. Verify: (a) `ava_windows` shows a new modal window titled per `WebViewLogin_WindowTitle`, owner disabled; (b) `ava_vm` on the login window reads `IsInitialized == true` and `NavigationCompletedCount >= 1` and `LastNavigationUrl` ≈ `about:blank` (navigation fired); (c) clicking `CancelButton` (or Esc) closes it and re-enables the owner. Capture the chrome with `ava_screenshot` → `D:\temp2\cbuild-mig\shots\webview-login-light-ava.png` and (theme-toggle) `…-dark-ava.png`. Record: the WebView area is blank/host-chrome (native HWND, expected).

- [x] **Step 8: Commit.**

```
git add src/CSUploader.Avalonia/Views/WebView2Host.cs src/CSUploader.Avalonia/Views/WebViewLoginWindow.axaml src/CSUploader.Avalonia/Views/WebViewLoginWindow.axaml.cs src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml src/CSUploader.Avalonia/DevTools/GalleryWindow.axaml.cs tests/CSUploader.Avalonia.Tests/Views/WebViewLoginWindowTests.cs
git commit -m "feat(avalonia): Phase 8 Task 3 - WebView2 login host + window shell (controller lifecycle, bounds, proxy/cert, teardown)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 4: Completion + capture (cookie / probe → InteractiveAuthResult, Close(result))

Wire the correctness-critical completion: the 1 s poll timer (Avalonia `DispatcherTimer` — design line 79), `NavigationCompleted` → poll, the cookie capture (via `WebViewLoginCapture.SelectCookies` from Task 1), the probe-script path (via `TryParseJsonString` + `BuildCookieHeader`), the single-completion `_completed` guard (rule 49), and `Close(result)` with the assembled `InteractiveAuthResult`. The pure decisions are already unit-tested (Task 1); this task is the plumbing + result assembly (review-verified; the live capture is the manual cutover sign-in).

**Files:**
- Modify: `src/CSUploader.Avalonia/Views/WebViewLoginWindow.axaml.cs`

**Interfaces:**
- Consumes: `WebViewLoginCapture.SelectCookies/TryParseJsonString/BuildCookieHeader` (Task 1), `CSUploader.Services.InteractiveAuthResult` (Core: `record struct InteractiveAuthResult(string SessionCookieValue, string? CapturedUsername, IReadOnlyDictionary<string,string>? AdditionalCookies = null, string? ProbeValue = null)`).
- Produces: the `WebViewLoginWindow` now completes via `Close(new InteractiveAuthResult(...))` on capture — the contract `AvaloniaWebViewInteractiveAuthService` (Task 6) awaits.

- [x] **Step 1: Add the completion fields + usings.** In `WebViewLoginWindow.axaml.cs`, add to the usings: `using System.Linq;` and `using Avalonia.Threading;`. Add fields beside `_creating`:

```csharp
    private DispatcherTimer? _pollTimer;
    private bool _completed;

    /// <summary>Poll cadence. XFS-family hosters complete via POST->302 (NavigationCompleted already catches
    /// the cookie), but SPA hosters (FileBoom) log in via XHR + history.pushState with no NavigationCompleted,
    /// so the poll is their ONLY signal. 1 s balances latency vs cookie-store read pressure. (WPF: 1 s.)</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
```

- [x] **Step 2: Start the poll timer in `OnHwndReady`.** Immediately AFTER the `_core.SourceChanged += CoreWebView2_SourceChanged;` line, insert:

```csharp
            // Completion poll (Avalonia DispatcherTimer, stopped-ctor + explicit Start). Fires alongside
            // NavigationCompleted because SPA-shaped hosters change post-login state with no navigation event.
            // Idempotent via _completed.
            _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
            _pollTimer.Tick += async (_, _) => await PollForCompletionAsync();
            _pollTimer.Start();
```

- [x] **Step 3: Stop the timer in teardown.** In `TeardownController`, at the very top (before detaching `_core` handlers) insert:

```csharp
        _pollTimer?.Stop();
        _pollTimer = null;
```

- [x] **Step 4: Make `NavigationCompleted` also poll.** Replace the whole `CoreWebView2_NavigationCompleted` method with:

```csharp
    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _vm.RecordNavigationCompleted(_core?.Source);
        await PollForCompletionAsync();
    }
```

- [x] **Step 5: Add the completion/capture methods.** Add at the end of the class (before the closing brace):

```csharp
    // ---- Completion / capture (mirrors WebViewLoginWindow.xaml.cs:302-502; single-completion guard = rule 49) --

    /// <summary>Per-tick check: the JS probe for probe-script hosters (HitFile), else the cookie-jar read.</summary>
    private Task PollForCompletionAsync()
        => _successProbeScript is not null ? TryProbeAsync() : TryCaptureCookiesAsync();

    private async Task TryCaptureCookiesAsync()
    {
        if (_completed || _core is null)
        {
            return;
        }

        try
        {
            // CookieManager returns ALL cookies a request to _loginUrl would send — incl. HttpOnly (FileBoom's
            // accessToken); the HttpOnly flag only gates document.cookie, not CookieManager.
            IReadOnlyList<CoreWebView2Cookie> cookies = await _core.CookieManager.GetCookiesAsync(_loginUrl);

            CookieSelection sel = WebViewLoginCapture.SelectCookies(
                cookies.Select(c => (c.Name, c.Value)),
                _cookieName, _usernameCookieName, _additionalCookieNames, _cookieValueValidator);

            if (sel.SessionValue is null || _completed)
            {
                return; // not signed in yet (or lost the race) — keep polling
            }

            _completed = true;
            _pollTimer?.Stop();
            _vm.IsCompleted = true;
            Close(new InteractiveAuthResult(sel.SessionValue, sel.UsernameValue, sel.AdditionalCookies));
        }
        catch (Exception ex)
        {
            // Transient cookie-read failure — the next nav/poll retries; just surface the diagnostic.
            _vm.Status = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Status_CookieReadFailed_Format"], ex.Message);
        }
    }

    private async Task TryProbeAsync()
    {
        if (_completed || _successProbeScript is null || _core is null)
        {
            return;
        }

        try
        {
            string raw = await _core.ExecuteScriptAsync(_successProbeScript);
            string? value = WebViewLoginCapture.TryParseJsonString(raw);
            if (string.IsNullOrEmpty(value) || _completed)
            {
                return; // page not authenticated yet (or lost the race)
            }

            _completed = true;
            _pollTimer?.Stop();
            _vm.IsCompleted = true;

            // Probe hosters can ALSO ask us to hand the logged-in cookie jar to the C# side (HitFile refresh).
            // HttpOnly included (CookieManager, not document.cookie). Best-effort — a failure here must not
            // block an otherwise-successful sign-in.
            string? cookieHeader = null;
            if (_cookieCaptureUrl is not null)
            {
                try
                {
                    IReadOnlyList<CoreWebView2Cookie> jar = await _core.CookieManager.GetCookiesAsync(_cookieCaptureUrl);
                    cookieHeader = WebViewLoginCapture.BuildCookieHeader(jar.Select(c => (c.Name, c.Value)));
                }
                catch
                {
                    // Leave cookieHeader null — sign-in still succeeds via the probe value.
                }
            }

            Close(new InteractiveAuthResult(cookieHeader ?? string.Empty, null, null, value));
        }
        catch (Exception ex)
        {
            _vm.Status = string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Status_CookieReadFailed_Format"], ex.Message);
        }
    }
```

- [x] **Step 6: Build + run the suite.** `dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\ava` (0 warnings) and `dotnet test … -p:OutDir=D:\temp2\cbuild-mig\ava-tests` (green, count unchanged — the pure capture logic is already covered by Task 1; the Task 3 ctor test still passes). Re-run the Task 3 Step 7 bridge demo against `about:blank`: it navigates, `IsCompleted` stays false (no `xfss` cookie), the window stays open, Cancel closes it — confirming the poll runs without false-completing.
      <br/>**Executor note (Task 4):** forced rebuild (`-t:Rebuild`) 0 warnings/0 errors; Avalonia suite 444/444 (unchanged, as designed); WPF/shared 1201/1201. The live bridge re-run needs a shown window → native WebView2 controller (foreground grab a background agent is refused — the plan's agent-safety constraint); it is reasoning-verified here and remains the maintainer's cutover check. Against `about:blank` the poll's `TryCaptureCookiesAsync` reads an empty jar → `SelectCookies` returns null session → `IsCompleted` stays false → window stays open; Cancel/Esc → `Close(null)`.

- [x] **Step 7: Commit.**

```
git add src/CSUploader.Avalonia/Views/WebViewLoginWindow.axaml.cs
git commit -m "feat(avalonia): Phase 8 Task 4 - WebView login completion (cookie/probe capture -> InteractiveAuthResult, Close(result))

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 5: Focus integration (the ADAPTATION ADDITION)

The spike never exercised focus, so hosting a raw `CoreWebView2Controller` (vs a WebView2 control) needs the three focus behaviors the design's ADAPTATION ADDITION calls out, plus the loop-closer: (1) `MoveFocusRequested` (Tab past the last / Shift+Tab before the first page field → move Avalonia focus to Cancel); (2) focus-on-activation (window `Activated` → `controller.MoveFocus(Programmatic)`); (3) initial focus (after the first NavigationCompleted → `MoveFocus`); (4) the Cancel→page loop-closer (`Host` GotFocus → `MoveFocus`). Native focus/typing is agent-unverifiable (foreground-grab refusal) — this is the manual cutover check — so this task is implement + review + regression-suite + record the maintainer-only item; there is NO headless test (a fabricated trivial one would add no value).

**Files:**
- Modify: `src/CSUploader.Avalonia/Views/WebViewLoginWindow.axaml.cs`

**Interfaces:**
- Consumes: `Microsoft.Web.WebView2.Core` — `CoreWebView2Controller.MoveFocus(CoreWebView2MoveFocusReason)`, `CoreWebView2Controller.MoveFocusRequested` (`EventHandler<CoreWebView2MoveFocusRequestedEventArgs>` with `.Reason`/`.Handled`), `CoreWebView2MoveFocusReason.{Programmatic,Next,Previous}` (all pinned present in the SDK XML — Reality-check register).

- [ ] **Step 1: Add the initial-focus flag.** In `WebViewLoginWindow.axaml.cs`, add a field beside `_completed`:

```csharp
    private bool _initialFocusPending;
```

- [ ] **Step 2: Wire focus-on-activation in the ctor.** In the constructor, after the `Closed += (_, _) => TeardownController();` line, add:

```csharp
        // Focus-on-activation (ADAPTATION ADDITION): alt-tabbing back into the login window pushes keyboard
        // focus into the page. Guarded — an Activated that fires before the controller exists is a no-op; the
        // explicit initial-focus (below, after the first navigation) covers the first show.
        Activated += (_, _) => _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

        // Loop-closer: when Avalonia focus lands back on the host (Tab off the Cancel button), hand it into the
        // page — completing the page <-> Cancel tab loop opened by MoveFocusRequested below.
        Host.GotFocus += (_, _) => _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
```

- [ ] **Step 3: Wire `MoveFocusRequested` + arm initial focus in `OnHwndReady`.** In `OnHwndReady`, immediately AFTER `_core = _controller.CoreWebView2;`, add:

```csharp
            // Tab-out of the page (ADAPTATION ADDITION): when the WebView asks to move focus out (Tab past the
            // last field = Next, Shift+Tab before the first = Previous), move Avalonia focus to the only other
            // focusable — the Cancel button — and mark handled so the WebView doesn't beep. Tabbing off Cancel
            // returns to Host (GotFocus above pushes focus back into the page).
            _controller.MoveFocusRequested += (_, e) =>
            {
                if (e.Reason is CoreWebView2MoveFocusReason.Next or CoreWebView2MoveFocusReason.Previous)
                {
                    CancelButton.Focus();
                    e.Handled = true;
                }
            };
            _initialFocusPending = true;
```

- [ ] **Step 4: Perform initial focus on the first NavigationCompleted.** In `CoreWebView2_NavigationCompleted`, after `_vm.RecordNavigationCompleted(_core?.Source);` and before `await PollForCompletionAsync();`, add:

```csharp
        if (_initialFocusPending)
        {
            _initialFocusPending = false;
            _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); // initial focus (ADAPTATION ADDITION)
        }
```

- [ ] **Step 5: Build + regression suite + bridge smoke.** `dotnet build … -p:OutDir=D:\temp2\cbuild-mig\ava` (0 warnings); `dotnet test … -p:OutDir=D:\temp2\cbuild-mig\ava-tests` (green, unchanged count). Re-run the gallery demo (Task 3 Step 7): the window still opens against `about:blank`, navigation fires, Cancel/Esc closes — confirming the focus wiring didn't break open/close mechanics. Record for the gate: native Tab-out/typing/initial-focus behavior is **maintainer-verified** (foreground-grab refusal; same class as the spike's unexercised typing).

- [ ] **Step 6: Commit.**

```
git add src/CSUploader.Avalonia/Views/WebViewLoginWindow.axaml.cs
git commit -m "feat(avalonia): Phase 8 Task 5 - WebView login focus integration (MoveFocusRequested, focus-on-activation, initial focus)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 6: AvaloniaWebViewInteractiveAuthService + DI swap (TDD)

Port the WPF `WebViewInteractiveAuthService` (`src/Services/WebViewInteractiveAuthService.cs`): the per-hoster serialization gate, the null-proxy fail-fast, the SOCKS-with-auth refusal (via Task 1 + `IDialogService`), and the modal open. It sheds its WPF `Application.Current.Dispatcher` for the injected `IUiDispatcher` (design line 79) and resolves the owner (reveal-or-own) via `DialogOwnerResolver` + `ITrayIconService` — Avalonia `ShowDialog` rejects a null/hidden owner. The window now assembles the result (rule 49), so the service just awaits `ShowDialog<InteractiveAuthResult?>`. Then flip the DI registration off the throwing stub and delete the stub.

**Files:**
- Create: `src/CSUploader.Avalonia/Services/AvaloniaWebViewInteractiveAuthService.cs`
- Delete: `src/CSUploader.Avalonia/Services/StubInteractiveAuthService.cs`
- Modify: `src/CSUploader.Avalonia/App.axaml.cs` (DI line 256)
- Test: `tests/CSUploader.Avalonia.Tests/Services/AvaloniaWebViewInteractiveAuthServiceTests.cs`

**Interfaces:**
- Produces: `CSUploader.Services.AvaloniaWebViewInteractiveAuthService : IInteractiveAuthService` — ctor `(IDialogService dialogService, AppSettings settings, IUiDispatcher dispatcher, ITrayIconService trayIcon)`; `Task<InteractiveAuthResult?> AcquireSessionCookieAsync(InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)`.
- Consumes: `WebViewLoginWindow` ctor + `ShowDialog<InteractiveAuthResult?>` (Task 3/4), `WebViewLoginProxy.ResolveProxyCredentials`/`ProxyCredentials` (Task 1), `DialogOwnerResolver.ResolveFromLifetime` (`src/CSUploader.Avalonia/Services/DialogOwnerResolver.cs`), `ITrayIconService.ShowMainWindow`, `IDialogService.ShowErrorAsync`, `Localizer`.

- [ ] **Step 1: Write the failing service tests.** Create `tests/CSUploader.Avalonia.Tests/Services/AvaloniaWebViewInteractiveAuthServiceTests.cs`:

```csharp
// <copyright file="AvaloniaWebViewInteractiveAuthServiceTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net;
using CSUploader.Lib.Net;
using CSUploader.Services;
using CSUploader.Upload;
using Moq;

namespace CSUploader.Tests.Avalonia.Services;

/// <summary>
/// The two headless-reachable branches of the Avalonia interactive-auth service (Phase 8 Task 6): the
/// null-proxy fail-fast and the SOCKS-with-auth refusal — both short-circuit BEFORE any window is created
/// (so no live WebView2 / desktop lifetime is needed). The success path opens a native WebView and is the maintainer's
/// cutover sign-in. Uses an inline IUiDispatcher (runs the marshal action synchronously).
/// </summary>
public class AvaloniaWebViewInteractiveAuthServiceTests
{
    private static InteractiveAuthSpec Spec() =>
        new("ex-load", "https://ex-load.com/login.html", ".ex-load.com", "xfss");

    [Fact]
    public async Task NullProxy_ReturnsNull_WithoutDispatchOrError()
    {
        var dialog = new Mock<IDialogService>(MockBehavior.Strict);
        var service = new AvaloniaWebViewInteractiveAuthService(
            dialog.Object, new AppSettings(), new InlineDispatcher(), Mock.Of<ITrayIconService>());

        InteractiveAuthResult? result = await service.AcquireSessionCookieAsync(
            Spec(), username: "u", proxy: null, CancellationToken.None);

        Assert.Null(result);
        dialog.VerifyNoOtherCalls(); // fail-fast: no error dialog on the null-proxy path
    }

    [Fact]
    public async Task SocksWithAuth_ShowsRefusal_ReturnsNull_WithoutWindow()
    {
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var socks = new ProxyChoice(9,
            new WebProxy("socks5://p:1080") { Credentials = new NetworkCredential("u", "pw") },
            "socks5://p:1080");
        var service = new AvaloniaWebViewInteractiveAuthService(
            dialog.Object, new AppSettings(), new InlineDispatcher(), Mock.Of<ITrayIconService>());

        InteractiveAuthResult? result = await service.AcquireSessionCookieAsync(
            Spec(), username: "u", proxy: socks, CancellationToken.None);

        Assert.Null(result);
        dialog.Verify(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    // Inline IUiDispatcher: runs the marshal action synchronously so the SOCKS refusal (which never opens a
    // window) resolves without a real UI thread. Timer/Post are unused on these paths.
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run it to verify it fails.** Expected: compile failure (`AvaloniaWebViewInteractiveAuthService` does not exist).

- [ ] **Step 3: Implement the service.** Create `src/CSUploader.Avalonia/Services/AvaloniaWebViewInteractiveAuthService.cs`:

```csharp
// <copyright file="AvaloniaWebViewInteractiveAuthService.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Controls;
using CSUploader.Lib.Localization;
using CSUploader.Lib.Net;
using CSUploader.Upload;
using CSUploader.Views;

namespace CSUploader.Services;

/// <summary>
/// Avalonia <see cref="IInteractiveAuthService"/> (port of the WPF WebViewInteractiveAuthService). Opens a
/// modal <see cref="WebViewLoginWindow"/> on the UI thread to capture the session cookie / probe value,
/// routing the embedded browser through the same proxy uploads will use (XFS binds session cookies to the
/// issuing IP). Serialises concurrent calls per hoster so a burst of background uploads doesn't stack N
/// modal windows. Sheds the WPF dispatcher for <see cref="IUiDispatcher"/> (design line 79); resolves the
/// owner (reveal-or-own) via <see cref="DialogOwnerResolver"/> since Avalonia ShowDialog rejects a null /
/// hidden owner and this app hides its main window to the tray.
/// </summary>
public sealed class AvaloniaWebViewInteractiveAuthService(
    IDialogService dialogService,
    AppSettings settings,
    IUiDispatcher dispatcher,
    ITrayIconService trayIcon) : IInteractiveAuthService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perHosterGates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<InteractiveAuthResult?> AcquireSessionCookieAsync(
        InteractiveAuthSpec spec, string username, ProxyChoice? proxy, CancellationToken cancellationToken)
    {
        _ = username; // informational (parity with WPF — a future impl could pre-fill the form)

        // Null proxy = "Use Proxies is on but no usable proxy is available" — mirror the upload fail-fast.
        if (proxy is null)
        {
            return null;
        }

        // Per-hoster gate: different hosters sign in in parallel (separate windows / user-data folders), but
        // two uploads to the SAME hoster share one dialog.
        SemaphoreSlim gate = _perHosterGates.GetOrAdd(spec.HosterName, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Marshal onto the UI thread (typically called from an upload thread-pool thread). The action just
            // STARTS the async show and bridges it to a TCS; the try/catch makes the async-void body escape-proof
            // (an unsunk async-void exception would otherwise crash the dispatcher loop). The gate is held for the
            // dialog's whole lifetime (await tcs.Task), preserving the "one dialog per hoster" invariant.
            TaskCompletionSource<InteractiveAuthResult?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await dispatcher.InvokeAsync(() =>
            {
                async void Pump()
                {
                    try
                    {
                        tcs.TrySetResult(await ShowLoginWindowAsync(spec, proxy));
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }

                Pump();
            }).ConfigureAwait(false);

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<InteractiveAuthResult?> ShowLoginWindowAsync(InteractiveAuthSpec spec, ProxyChoice proxy)
    {
        ProxyResolution resolution = WebViewLoginProxy.ResolveProxyCredentials(proxy);
        if (resolution.SocksAuthUnsupported)
        {
            // SOCKS-with-auth: Chromium can't carry SOCKS creds and there's no event to supply them. Be honest
            // rather than silently using an unauthenticated SOCKS hop, and return null so the pipeline surfaces
            // a failed sign-in.
            await dialogService.ShowErrorAsync(
                string.Format(CultureInfo.CurrentCulture, Localizer.Instance["WebViewLogin_Error_SocksAuthUnsupported_Format"], proxy.Description),
                Localizer.Instance["WebViewLogin_Error_UnsupportedProxy_Title"]);
            return null;
        }

        Window? owner = ResolveOwnerOrReveal();
        if (owner is null)
        {
            return null; // no window available to own the modal (headless / no lifetime)
        }

        WebViewLoginWindow window = new(
            spec.HosterName,
            spec.LoginUrl,
            spec.CookieDomain,
            spec.CookieName,
            usernameCookieName: spec.UsernameCookieName,
            proxy: proxy,
            proxyCredentials: resolution.Credentials,
            cookieValueValidator: spec.CookieValueValidator,
            additionalCookieNames: spec.AdditionalCookieNames,
            successProbeScript: spec.SuccessProbeScript,
            cookieCaptureUrl: spec.CookieCaptureUrl,
            userAgentOverride: spec.UserAgentOverride,
            allowInvalidCertificates: settings.AllowInvalidServerCertificates);

        return await window.ShowDialog<InteractiveAuthResult?>(owner);
    }

    // Reveal-or-own (mirrors AvaloniaDialogService.GetOwnerOrRevealAsync): a modal demands a visible parent, so
    // a tray-hidden main window is revealed first. Null only under a non-desktop lifetime (headless).
    private Window? ResolveOwnerOrReveal()
    {
        Window? owner = DialogOwnerResolver.ResolveFromLifetime();
        if (owner is null)
        {
            trayIcon.ShowMainWindow();
            owner = DialogOwnerResolver.ResolveFromLifetime();
        }

        return owner;
    }
}
```

- [ ] **Step 4: Run the service tests to verify they pass.** Expected: the 2 facts PASS.

- [ ] **Step 5: Swap the DI registration + delete the stub.** In `src/CSUploader.Avalonia/App.axaml.cs`, replace line 256:

```csharp
        services.AddSingleton<IInteractiveAuthService, StubInteractiveAuthService>(); // throws until Phase 8
```

with:

```csharp
        services.AddSingleton<IInteractiveAuthService, AvaloniaWebViewInteractiveAuthService>(); // real WebView2 sign-in (Phase 8)
```

Then delete `src/CSUploader.Avalonia/Services/StubInteractiveAuthService.cs`.

- [ ] **Step 6: Build + full suite + DI smoke.** `dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\ava` (0 warnings; verifies no dangling `StubInteractiveAuthService` reference). `dotnet test … -p:OutDir=D:\temp2\cbuild-mig\ava-tests` (green; the head DI smoke test — which composes the provider — still resolves `IInteractiveAuthService`). Record the new Avalonia total.

- [ ] **Step 7: Commit.**

```
git add src/CSUploader.Avalonia/Services/AvaloniaWebViewInteractiveAuthService.cs src/CSUploader.Avalonia/App.axaml.cs tests/CSUploader.Avalonia.Tests/Services/AvaloniaWebViewInteractiveAuthServiceTests.cs
git rm src/CSUploader.Avalonia/Services/StubInteractiveAuthService.cs
git commit -m "feat(avalonia): Phase 8 Task 6 - AvaloniaWebViewInteractiveAuthService + DI swap off the stub

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 7: Retire the Phase 2 spike

The spike (`src/CSUploader.Avalonia/Spike/`) was the throwaway reference; the real host + window now supersede it. Delete it, remove its `--webview-spike` launch flag from `App.axaml.cs`, and refresh the csproj comments that reference the spike (the WebView2 package ref, `_DropWebView2DesktopWrappers`, `BuiltInComInteropSupport`, `ApplicationManifest` are all PERMANENT — keep them, drop only the "spike/THROWAWAY-adjacent" wording).

**Files:**
- Delete: `src/CSUploader.Avalonia/Spike/WebView2HwndHost.cs`, `WebView2SpikeWindow.axaml`, `WebView2SpikeWindow.axaml.cs`
- Modify: `src/CSUploader.Avalonia/App.axaml.cs` (remove the `--webview-spike` flag + its `Opened`-hook launch)
- Modify: `src/CSUploader.Avalonia/CSUploader.Avalonia.csproj` (comment refresh only)

- [ ] **Step 1: Delete the spike directory.** `git rm src/CSUploader.Avalonia/Spike/WebView2HwndHost.cs src/CSUploader.Avalonia/Spike/WebView2SpikeWindow.axaml src/CSUploader.Avalonia/Spike/WebView2SpikeWindow.axaml.cs`.

- [ ] **Step 2: Remove the `--webview-spike` flag AND every stale reference from `App.axaml.cs`.** The literal `webview-spike` survives in COMMENTS, not just code — Task 8's `grep "webview-spike" → zero` and `grep "Spike" → zero` gates would fail on otherwise-clean code, so scrub ALL of these (cited against the current file):
  1. **The DEBUG flag decl** (`App.axaml.cs:106`) — delete the `bool webviewSpike = …` line so only `gallery` remains.
  2. **The `#if DEBUG` flag comment block** (`:102-105`) — it describes both the spike and the gallery; rewrite it to describe only the gallery. Replace the whole block + the `gallery` decl (`:102-107`) with:

```csharp
#if DEBUG
            // DEBUG-only dev flag, opened from the Opened hook below: the dev gallery (--gallery, non-modal).
            // Declared under #if DEBUG so it never OPENS anything in Release; the window type ships as dead
            // code (trigger-gated convention).
            bool gallery = desktop.Args?.Contains("--gallery", StringComparer.Ordinal) == true;
#endif
```

  3. **The `Opened`-hook launch block** (`:208-213`) — delete the entire `if (webviewSpike) { … await new Spike.WebView2SpikeWindow().ShowDialog(mainWindow); }` block. The gallery `WebViewLoginDemoButton` (Task 3) is now the demo surface. Leave the `if (gallery) { … }` block intact.
  4. **The one-shot-guard comment** (`:142`) — "deliberately skips the post-init UpdateVisibility / --gallery / --webview-spike re-runs on a" → drop the `/ --webview-spike`: "deliberately skips the post-init UpdateVisibility / --gallery re-runs on a".
  5. **The startup-failure catch comment** (`:166`) — "Skip the post-init steps below (tray sync, spike) — they assume a hydrated ViewModel." → "Skip the post-init steps below (tray sync, gallery) — they assume a hydrated ViewModel." (After Task 7 the post-init steps are tray sync + gallery; "spike" is stale.)

- [ ] **Step 3: Refresh the csproj comments (no functional change).** In `src/CSUploader.Avalonia/CSUploader.Avalonia.csproj`:
  - The `<BuiltInComInteropSupport>` comment "required for the Phase 2 spike + Phase 8 login host." → "required for the WebView2 login host (CoreWebView2 COM marshaling)."
  - The `<ApplicationManifest>` comment "(the Phase 2 spike surfaced its absence; the Phase 8 login host needs it too)." → "(required for Avalonia NativeControlHost / WebView2 hosting)."
  - The `Microsoft.Web.WebView2` PackageReference comment "Phase 2 WebView2 GO/NO-GO spike (Spike/, THROWAWAY) + the Phase 8 login host." → "WebView2 login host (Core wrapper only; see _DropWebView2DesktopWrappers)."
  - The `_DropWebView2DesktopWrappers` target comment "THROWAWAY-adjacent: revisit at the Phase 8 login host." → "Permanent: the Avalonia head consumes only Microsoft.Web.WebView2.Core."

- [ ] **Step 4: Build + full suite + no-flag launch smoke.** `dotnet build … -p:OutDir=D:\temp2\cbuild-mig\ava` (0 warnings — confirms no dangling `Spike.` reference). `dotnet test … -p:OutDir=D:\temp2\cbuild-mig\ava-tests` (green, unchanged count). PowerShell-build to a scratch OutDir and launch WITHOUT flags: the app comes up with four MainWindow tabs, no gallery/spike surface, `--webview-spike` now inert.

- [ ] **Step 5: Commit.**

```
git add src/CSUploader.Avalonia/App.axaml.cs src/CSUploader.Avalonia/CSUploader.Avalonia.csproj
git rm src/CSUploader.Avalonia/Spike/WebView2HwndHost.cs src/CSUploader.Avalonia/Spike/WebView2SpikeWindow.axaml src/CSUploader.Avalonia/Spike/WebView2SpikeWindow.axaml.cs
git commit -m "chore(avalonia): Phase 8 Task 7 - retire the Phase 2 WebView2 spike

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Task 8: Phase gate — review, tag, reconcile

- [ ] **Step 1: Whole-diff review.** `git diff phase7-shell-ready..HEAD` by a fresh adversarial reviewer (whole-diff panels catch cross-task issues). Special attention: **the cookie-selection correctness** (a wrong session pick = a silent anonymous session — the ex-load/Hxfile findings; verify `SelectCookies` matches WPF's foreach incl. the empty-value skip, first-match, validator gate); the session-cookie **clear-on-open** (present, symmetric with the capture read, safe-for-all-hosters); the **single-completion guard** (rule 49 — only the first poll/nav caller stops the timer + `Close`s; no double-Close); the **result assembly** (probe path: `SessionCookieValue = cookieHeader ?? ""`, `ProbeValue = value`, others null; cookie path: session/username/additional); the **IUiDispatcher marshal** (the async-void `Pump` is escape-proof via try/catch; gate held for the dialog lifetime); the **reveal-or-own owner** (matches AvaloniaDialogService); **bounds sync** (DIP × RenderScaling, `_lastBounds` guard, all three sync triggers); **teardown** (`controller.Close()` before the child HWND dies; timer stopped first); the **focus wiring** (MoveFocusRequested→Cancel+Handled; Activated/Host.GotFocus→MoveFocus; initial-focus one-shot).
- [ ] **Step 2: Mechanical gates.**
  - `grep -rn "Spike" src/CSUploader.Avalonia/` → zero (case-sensitive; the NoSpike-leftover check — the deleted spike TYPES/namespace `WebView2SpikeWindow` / `WebView2HwndHost` / `Spike.` are gone, incl. the promoted host's rewritten docstring per Task 3 Step 1(d)). NOTE: this is deliberately case-sensitive — lowercase "spike" PROVENANCE comments in the ported window (e.g. "the Phase 2 spike recipe" crediting the validated bounds-sync source) are legitimate and pass; they are notes about where the code came from, not references to the deleted type.
  - `grep -rn "webview-spike" src/CSUploader.Avalonia/` → zero (the flag decl + all comment mentions scrubbed — Task 7 Step 2).
  - `grep -rn "StubInteractiveAuthService" src/` → zero (deleted; no stale reference).
  - `grep -rn "System.Windows" src/CSUploader.Avalonia/` → zero (the login host uses only `Microsoft.Web.WebView2.Core` + Avalonia + `System.Runtime.InteropServices`/`System.Drawing.Rectangle`, no WPF).
  - Both suites green; final counts recorded (WPF/shared 1201 unchanged; Avalonia = 418 + Task 1 (~19: 12 capture + 7 proxy cases) + Task 2 (3) + Task 3 (1) + Task 6 (2) = ~443, adjusted to the real running totals recorded per task).
  - i18n gate green (`python scripts/md-to-resx.py --check`); the phase diff shows zero `Strings*.resx` changes.
  - **Core-touch gate:** `git diff phase7-shell-ready..HEAD -- src/CSUploader.Core/` is EMPTY (this phase's promise — no Core touch, no DECISION needed, unlike Phase 7's ShowInfo).
  - **WPF-head safety:** `git diff phase7-shell-ready..HEAD -- src/` outside `src/CSUploader.Avalonia/**` is EMPTY. WPF Release build succeeds; WPF suite 1201.
  - Avalonia Release build succeeds; launched WITHOUT flags: four MainWindow tabs, no gallery/spike surface, and `IInteractiveAuthService` resolves to the real service (the EditAccountWindow "Sign in" button is enabled for a WebView hoster — the callback is non-null).
- [ ] **Step 3: Contact sheet.** `webview-login` (light+dark, Avalonia only — the login window CHROME via the gallery `WebViewLoginDemoButton` against `about:blank`). Read each shot; append to the accepted-divergence list: the WebView content area is BLANK/host-chrome on both shots (a native HWND, invisible to bridge screenshots — design line 88, NOT a regression); chrome parity (header / instructions / status strip / Cancel) is verified against the WPF XAML `src/Views/WebViewLoginWindow.xaml` (no `-wpf` cell — WPF head untouched).
- [ ] **Step 4:** `git tag phase8-webview-login-ready`.
- [ ] **Step 5: Reconcile the design doc** (`docs/superpowers/specs/2026-07-10-avalonia-migration-design.md`) with Phase 8's outcomes — at minimum: the narrow-seam finding (only `IInteractiveAuthService` was stubbed; the login callback was already Core-wired); the `Close(result)` completion plumbing (rule 49); the focus integration shipped (MoveFocusRequested→Cancel / Activated+Host.GotFocus→MoveFocus / initial-focus one-shot) with native focus/typing + Turnstile + 125/150% DPI remaining maintainer-only; the spike retired (host promoted to `Views/WebView2Host.cs`; `app.manifest` + `_DropWebView2DesktopWrappers` are permanent); Core untouched. **Also record this standing constraint (reviewer note): the interactive-auth seam now depends on async-all-the-way UI-thread callers.** Avalonia's `ShowDialog` does NOT spin a nested message loop (unlike WPF's, which pumped inline), so a future synchronous `.Result`/`.Wait()` on the UI thread against `AcquireSessionCookieAsync` (or any `ShowDialog<T>`-backed dialog service member) would DEADLOCK where the WPF head did not — the whole callback chain (`SettingsViewModel.InteractiveLoginAsync` → `AccountVerifier` → pipeline) must stay `await`-based. Note the Avalonia head is now feature-complete pending Phase 9 cutover. Commit — `"docs: reconcile design with Phase 8 outcomes (WebView login host, focus integration, spike retired)"`.
- [ ] **Step 6: Surface to the maintainer** (via the team lead): the contact-sheet path; the narrow-seam confirmation; that Core + the WPF head were untouched; the accepted divergences (uncapturable native WebView content); and the **standing maintainer-only manual checks** that a background agent could not exercise — carried from the Phase 2 spike verdict + this phase's focus addition: (a) a full real sign-in on a live hoster incl. typing credentials + solving a Turnstile/hCaptcha challenge (foreground-grab refusal), (b) Tab-out of the page → Cancel and Tab back into the page (native focus), (c) 125%/150% DPI: the WebView fills the host with no dead zones and corner links hit-test correctly.

**Task 8 gate definition of done:** whole-diff reviewed; all mechanical gates green (incl. the NoSpike-leftover + StubInteractiveAuthService + System.Windows greps, the EMPTY Core-touch diff, the EMPTY WPF-head diff, both suites, i18n, Release builds + no-flag launch smoke); contact sheet complete + divergences listed; `phase8-webview-login-ready` tagged; design reconciled; the maintainer surfaced with the standing manual checks. After this the Avalonia head is feature-complete — only Phase 9 (staged cutover) remains.

---

## Reality-check register

Pinned during planning against the installed bits + the Phase 2 spike's live run — executors verify while coding (ILSpy per `dotnet-skills:ilspy-decompile` where noted), but no fallback is expected.

1. **WebView2 focus API surface** — CONFIRMED present in the installed SDK XML (`src/CSUploader.Avalonia/bin/.../Microsoft.Web.WebView2.Core.xml`): `CoreWebView2Controller.MoveFocus(CoreWebView2MoveFocusReason)`, `CoreWebView2Controller.MoveFocusRequested`, `CoreWebView2MoveFocusRequestedEventArgs.{Reason,Handled}`, `CoreWebView2MoveFocusReason.{Programmatic,Next,Previous}`, `CoreWebView2Controller.GotFocus/LostFocus`. RUNTIME behavior (native Tab-out actually raising MoveFocusRequested; MoveFocus actually focusing a page field) is **maintainer-only** (foreground-grab refusal) — the mechanism is wired faithfully.
2. **Controller hosting, bounds, teardown, cookies** — all GO from the Phase 2 spike (verdict doc): `env.CreateCoreWebView2ControllerAsync(hwnd)`, `controller.Bounds` = host DIP × `RenderScaling` (NOT the child `GetClientRect`, which lags a resize), `controller.NotifyParentWindowPositionChanged()`, `controller.Close()` releasing the user-data-folder lock, `CookieManager.GetCookiesAsync` surfacing HttpOnly cookies. `NativeControlHost.LayoutUpdated` + `Window.PositionChanged` + `Window.ScalingChanged` + `Window.RenderScaling` all used by the spike.
3. **`app.manifest` + `_DropWebView2DesktopWrappers` + `BuiltInComInteropSupport`** — CONFIRMED present in `CSUploader.Avalonia.csproj` (shipped Phase 2, permanent). Phase 8 adds NO csproj functional change (comment refresh only).
4. **`MessageBoxWindow.ShowErrorAsync(Window? owner, string message, string title)`** — CONFIRMED `internal static`, same-assembly reachable from the login window (`MessageBoxWindow.axaml.cs:90`). Owner = the shown login window (in the `OnHwndReady` catch, the window is attached → shown).
5. **`Window.ShowDialog<TResult>(Window owner)`** with `TResult = InteractiveAuthResult?` (nullable value type): `Close(null)` / X / Esc-Cancel → `default` = `null`; `Close(new InteractiveAuthResult(...))` → the boxed value. CONFIRMED by the widespread `ShowDialog<FileHosterLoginDto?>` / `ShowDialog<MessageBoxOutcome>` usage (`AvaloniaDialogService`, `MessageBoxWindow`).
6. **`Button.IsCancel`** routes Esc through `Click` but does NOT auto-close (port rule 7) — the Cancel handler `Close(null)`s explicitly. CONFIRMED (`MessageBoxWindow.axaml.cs` remarks).
7. **`new AppSettings()`** parameterless — CONFIRMED constructible in tests (`AvaloniaDialogServiceTests.cs:31`). `ProxyChoice(int Id, IWebProxy? WebProxy, string Description)` + `ProxyChoice.Direct` — CONFIRMED (`src/CSUploader.Core/Lib/Net/ProxyChoice.cs`).
8. **`InitializeComponent()` is generator-emitted** — CONFIRMED (`EditAccountWindow.axaml.cs:35,90` and `MessageBoxWindow.axaml.cs:38` are `partial class … : Window` and call `InitializeComponent();` with NO hand-written body; the Avalonia source generator emits it from the paired `.axaml`). The login window MUST be `public partial class WebViewLoginWindow : Window` and just call `InitializeComponent();` — do NOT hand-write the method or reference `AvaloniaXamlLoader` (that is CS0111).

## Open questions / DECISIONS for the team lead

1. **Core untouched — confirming no DECISION is needed.** The plan is deliberately designed to touch ZERO Core files (the login callback is already Core-wired; the marshal uses the existing `IUiDispatcher.InvokeAsync(Action)` via a TCS bridge rather than adding a `Func<Task>` overload). If a reviewer prefers an `IUiDispatcher.InvokeAsync(Func<Task>)` Core overload over the TCS bridge, that becomes a Core touch requiring your sign-off — flag it; otherwise Core stays empty (Task 8 gate).
2. **No WPF reference shot for the login window.** The design lists `WebViewLoginWindow` for individual per-view sign-off, but its content is an un-screenshottable native HWND (design line 88), so this plan captures only the AVALONIA chrome (via the gallery demo) and verifies chrome parity by reading the WPF XAML — keeping the WPF head untouched and avoiding a live-WebView2 shot harness. Confirm you're OK with no `-wpf` contact-sheet cell for this view.
3. **Standing maintainer-only checks carried forward** (from the Phase 2 spike verdict + Task 5): native typing + one full real Turnstile sign-in; Tab-out/Tab-in native focus; 125%/150% DPI bounds. These are cutover-smoke items (Phase 9 step 2), not Phase 8 blockers — confirm that's the intended disposition.

## Self-review

- **Spec coverage** (design Phase 8 line 103 + line 79 adaptation list + line 88 verification): port window logic onto the spiked host → Tasks 3+4; DispatcherTimer → Task 4; init-failure message box → Task 3; DialogResult→Close(result) single-completion guard → Task 4 (rule 49); Loaded→controller-ready + EnsureCoreWebView2Async/Dispose→controller Create/Close → Task 3; real service sheds WPF dispatcher via IUiDispatcher + DI swap → Task 6; focus integration (MoveFocusRequested/focus-on-activation/initial focus) → Task 5; retire spike → Task 7; agent verifies open + navigation VM + open/close + unit tests for cookie/probe → Tasks 2/3 (VM + gallery demo) + Task 1 (pure capture tests). All covered. The GO/NO-GO judgment (no probe task; focus is a maintainer-verified refinement, not an architecture gate) is recorded in its own section.
- **Placeholder scan:** every code step carries complete code (window XAML + full code-behind; both helper classes; the VM; the service; all tests). No "TBD"/"add error handling"/"similar to Task N". The Task 4/5 edits give full method bodies. The one deliberate "verify at build" (Reality-check 8, `InitializeComponent` generated-vs-hand-written) is a known Avalonia ambiguity with both branches specified, not a placeholder.
- **Type consistency:** `WebViewLoginCapture.SelectCookies`/`TryParseJsonString`/`BuildCookieHeader` + `CookieSelection(SessionValue,UsernameValue,AdditionalCookies)` identical across Task 1 (def+tests) and Task 4 (window use). `WebViewLoginProxy.BuildProxyServerArg`/`SanitizeFolderName`/`ResolveProxyCredentials` + `ProxyResolution(Credentials,SocksAuthUnsupported)` + `ProxyCredentials(Username,Password)` identical across Task 1, Task 3 (window ctor), Task 6 (service). `WebViewLoginViewModel` property/method names identical across Task 2 (def+tests) and Task 3/4/5 (window). The `WebViewLoginWindow` 13-arg ctor is identical in Task 3 (def), Task 6 (service call), and the gallery demo. `InteractiveAuthResult(string SessionCookieValue, string? CapturedUsername, IReadOnlyDictionary<string,string>? AdditionalCookies=null, string? ProbeValue=null)` (Core) used consistently in Task 4's two `Close(...)` sites. `AvaloniaWebViewInteractiveAuthService(IDialogService,AppSettings,IUiDispatcher,ITrayIconService)` identical in Task 6's def, tests, and the `App.axaml.cs` DI swap.
