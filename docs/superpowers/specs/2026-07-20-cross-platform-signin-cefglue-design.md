# Cross-Platform Interactive Sign-In (CefGlue on Linux/macOS) — Design

**Status:** DRAFT (Codex-reviewed, iterating). Branch `linux-port`.

## Goal

Make the interactive captcha sign-in (`IInteractiveAuthService`) work on **Linux and macOS**, not just Windows — so captcha-gated hosters (Rapidgator, NitroFlare, HitFile, Buzzheavier, ex-load, Keep2Share/FileBoom, XFS family, …) can sign in on every platform. Windows keeps its shipped WebView2 implementation unchanged.

**Requirement (2026-07-20):** "works well everywhere / the same features are supported" — feature parity, NOT byte-identical engines. Chosen path = **Option B**: WebView2 on Windows, **CefGlue/Chromium on Linux+macOS**, behind the one existing `IInteractiveAuthService` seam.

**De-risk spike (2026-07-20): GREEN.** A standalone CefGlue.Avalonia 120.6099.211 app on Ubuntu-WSL (glibc, WSLg) rendered + solved Buzzheavier's Cloudflare Turnstile in Chromium-120 and read back the HttpOnly `xsession` cookie via `CefCookieManager.VisitUrlCookies(includeHttpOnly:true)`. The two make-or-break capabilities are proven.

## Non-goals

- **Not** replacing WebView2 on Windows (it works, is evergreen, zero bundle cost). No Windows behavior change.
- **Not** solving the `cf_clearance` → C# uploader JA3-fingerprint gap (pre-existing; unsolved by any embedded browser; only affects clearance-forwarding hosters).
- **Not** making Cloudflare *managed-challenge* hosters (TakeFile) work — they defeat even WebView2 and remain disabled.
- **Not** a Windows-side refactor of the existing WebView2 code beyond what the shared seam already provides.

## Architecture

`IInteractiveAuthService` (Core, unchanged contract) has two head implementations, selected at DI registration by target:

- **Windows (`#if WINDOWS`)** → `AvaloniaWebViewInteractiveAuthService` (existing, unchanged).
- **non-Windows (`#else`)** → **new `CefGlueInteractiveAuthService`** (replaces the current `UnsupportedInteractiveAuthService` stub).

Both are behind the same seam, so **Core, the pipelines, and the whole sign-in call chain are untouched**. The multi-target csproj (from the build-portability commit `b2ccb76`) already conditions Windows-only code out of the portable target; we extend that to add the CefGlue code to the portable target only.

### Engine + packages (Linux/macOS target only)

- `CefGlue.Avalonia` **120.6099.211** (pulls `CefGlue.Common` 120.6099.211 + `cef.redist.<rid>` = Chromium/CEF 120).
- `Avalonia.ReactiveUI` **11.3.9** — transitive dep of CefGlue.Avalonia; it tops out at 11.3.9 (11.3.18 fails restore, `NU1102`). Pin to the highest 11.3.x so it doesn't drag in 11.0.9. (Confirmed necessary in the spike.)
- Rendering is **OSR (off-screen)** — no `NativeControlHost` child window, which *removes* the Linux X11/Wayland embedding concern and the entire WebView2Host P/Invoke + `SyncBounds` + focus-marshaling layer.

## Components

### 1. `CefGlueInteractiveAuthService` (non-Windows)
Mirror of `AvaloniaWebViewInteractiveAuthService`, **reusing its engine-agnostic machinery verbatim**: per-hoster `SemaphoreSlim(1,1)` gate, UI-thread marshaling via `IUiDispatcher.InvokeAsync`, `DialogOwnerResolver.ResolveVisibleMainOnly()` owner, TCS bridge over `ShowDialog<InteractiveAuthResult?>`, cancellation-token re-check on the UI thread, and the **SOCKS-with-auth pre-window refusal** (kept verbatim — CEF has the same limitation). Only the window it opens differs.

### 2. `CefGlueLoginWindow` (non-Windows)
The CefGlue analog of `WebViewLoginWindow`. Hosts an `AvaloniaCefBrowser` (OSR), and re-implements the harvest against CEF. **Reuses unchanged** the pure helpers: `WebViewLoginCapture.SelectCookies`/`BuildCookieHeader` (cookie selection + validator gate + jar serialization), `WebViewLoginProxy` classification, and the `WebViewLoginViewModel`. The `_completed`/`_torndown` single-completion/teardown latches and the `Close(InteractiveAuthResult?)` result plumbing port unchanged.

### 3. Capability mapping (WebView2 → CefGlue/CEF)

| Concern | WebView2 (Windows, unchanged) | CefGlue/CEF (new, Linux/macOS) |
|---|---|---|
| Host/embedding | Raw HWND `NativeControlHost` + `SyncBounds` + `MoveFocus` | **Dropped** — `AvaloniaCefBrowser` OSR control handles layout/DPI/focus |
| Per-hoster isolation | per-window `CreateAsync(userDataFolder)` | **`CefRequestContext` with own `CachePath`** per login; the browser is CREATED with that context, and **all cookie + proxy operations go through THAT context's manager** (`requestContext.GetCookieManager(...)`), never `CefCookieManager.GetGlobal` (which reads the shared jar and would defeat isolation — R1 blocker) |
| Proxy server | `--proxy-server` cmdline arg | per-`CefRequestContext` `SetPreference("proxy", {mode:"fixed_servers", server})` |
| Proxy auth | `BasicAuthenticationRequested` event | `CefRequestHandler.GetAuthCredentials(isProxy:true, …)` (cleaner — distinguishes proxy vs origin) |
| SOCKS-with-auth | pre-window refusal | **same refusal, kept verbatim** (CEF also can't inject SOCKS creds) |
| Invalid cert | `ServerCertificateErrorDetected` → AlwaysAllow | `CefRequestHandler.OnCertificateError(...) → callback.Continue()` |
| User-Agent override | `Settings.UserAgent` (per-window) | CEF UA is process-global at init, so per-login override needs BOTH: a **per-browser DevTools `Network.setUserAgentOverride`** (makes `navigator.userAgent` + UA client hints consistent — the JS-visible UA Cloudflare reads) AND an `OnBeforeResourceLoad` `User-Agent` header rewrite for subresources. Header-only is INSUFFICIENT — it does not change `navigator.userAgent` (R1 should-fix). |
| **Read named/jar cookies (incl. HttpOnly)** | `CookieManager.GetCookiesAsync(url)` | **`requestContext.GetCookieManager(null).VisitUrlCookies(url, includeHttpOnly:true, visitor)`** — the LOGIN'S context manager (not global). Visitor runs on the CEF IO thread: copy primitive Name/Value/Domain/HttpOnly values there, complete a `TaskCompletionSource(RunContinuationsAsynchronously)`, handle zero-results/schedule-failure/teardown, then marshal ONCE to the UI thread. `CefCookie` exposes Name/Value/Domain/Path/Secure/HttpOnly. |
| Clear stale cookie pre-nav | `CookieManager.DeleteCookies(name, url)` (sync-ish) | the CONTEXT manager `DeleteCookies(url, name, callback)` — **async; AWAIT its completion callback BEFORE `LoadURL`** (else the delete races the navigation — R1 should-fix) |
| Navigate | `Navigate(url)` | `browser.GetMainFrame().LoadURL(url)` |
| Nav events | `NavigationCompleted` / `SourceChanged` | `LoadEnd`/`LoadingStateChange` / `AddressChanged` |
| Completion poll | 1s `DispatcherTimer` + nav events | same timer + nav events |
| **Capture full jar for `CookieCaptureUrl`** (probe hosters) | `CookieManager.GetCookiesAsync(CookieCaptureUrl)` → `BuildCookieHeader` | the CONTEXT manager `VisitUrlCookies(CookieCaptureUrl, includeHttpOnly:true, …)` (same IO-thread/TCS/marshal contract as above), serialized via the reused `BuildCookieHeader` |
| Cookie selection/validator/jar | `WebViewLoginCapture.*` | **reused unchanged** |
| **JS success-probe return** | `ExecuteScriptAsync` → JSON-encoded string; decoded by `TryParseJsonString` | **`browser.EvaluateJavaScript<string>(script)`** returns the value DIRECTLY (not JSON-quoted). **`TryParseJsonString` is BYPASSED on the CEF path** — treat empty/null as "not yet"; non-empty ⇒ success. (Highest API-shape divergence; isolate in a small shim.) |
| Completion/cancel/teardown | `_completed`/`_torndown`, `Close(result)`, dispose controller | same latches; **async close: `CloseBrowser(true)` → wait `OnBeforeClose` → release control/browser/context** (Lifecycle §); init-in-flight race guard (browser created after teardown → close it immediately, mirrors the WebView2 controller-race guard); **focus block dropped** (OSR control participates in Avalonia focus natively) |

### 4. CEF lifecycle, threading, teardown & gating

**Process init (once):** `CefRuntimeLoader.Initialize(new CefSettings{ RootCachePath=… })` inside `AppBuilder.…AfterSetup(…)` on non-Windows startup only (spike-confirmed ordering); record the init thread. The render subprocess (`Xilium.CefGlue.BrowserProcess`, self-contained net8) ships alongside. Sandbox: default `--no-sandbox --disable-gpu` (required under WSLg / no-GPU); a real desktop may allow the sandbox — a documented switch, off by default.

**Per-login context:** each login creates a `CefRequestContext` with its own `CachePath`; the `AvaloniaCefBrowser` is created BOUND to that context; the proxy preference is set on that context (after context init, on the required CEF thread); and every cookie op uses `requestContext.GetCookieManager(...)` — never the global manager.

**Thread affinity (load-bearing):** cookie visitors and delete callbacks fire on the CEF **IO thread**. Contract: copy primitives in the callback → complete a `TaskCompletionSource(RunContinuationsAsynchronously)` → marshal ONCE to the Avalonia UI thread before touching the window/VM. Handle (a) zero-results (the visitor may never fire for a cookieless URL — a TIMEOUT safety net, as the spike required), (b) schedule failure, (c) teardown-in-flight. `DeleteCookies` is likewise async — its callback MUST complete before `LoadURL`.

**Async teardown (R1 blocker):** closing is NOT synchronous. Sequence: set `_torndown` → `browser.CloseBrowser(true)` → wait for `OnBeforeClose` → release the control/browser → dispose the `CefRequestContext`. `CefRuntime.Shutdown()` runs at process exit ONLY after every browser has closed, on the init thread. **Init-in-flight race guard:** if teardown set `_torndown` while a browser was still being created (`OnAfterCreated` pending), immediately close the late browser — the direct analog of the WebView2 controller-race guard.

**Concurrency & gating (R1 blocker + should-fix):** the existing per-hoster `SemaphoreSlim(1,1)` gate lets DIFFERENT hosters run concurrently. That is only safe if per-`CefRequestContext` isolation + per-browser request handlers genuinely isolate two LIVE browsers — plausible but UNPROVEN. **Decision: the CEF head starts with a PROCESS-WIDE login gate (serialize ALL interactive logins)**, relaxing to the per-hoster gate only after a real two-browser concurrent test (distinct cookies/UAs, no cross-talk) passes. **Gate ownership:** hold the gate until the dialog/browser actually CLOSES — cancellation abandons only the caller-facing await (per the interface contract) and does NOT release the gate (else a second window opens beside the orphan).

## Packaging / per-platform

- **Linux (`linux-x64`, glibc):** ships `cef.redist.linux64` (~150–250 MB, incl. `libcef.so`, `.pak`s, `locales/`, the render subprocess). **System deps required on the target:** the Chromium set (libnss3, libgtk-3, libgbm, libasound2, libx11/xcb/xcomposite/xdamage/xrandr/xkbcommon/xshmfence, libdrm, libcups2, pango/cairo/atk, fonts) **plus the Avalonia X11 deps `libice6` + `libsm6`** (spike-critical miss). Alpine/musl is unsupported (glibc-only CEF).
- **macOS (`osx-x64` / `osx-arm64`):** `cef.redist.osx*`; CEF app-bundle/helper structure + entitlements (OSR). Known **macOS OSR CJK-IME crash** (Avalonia #14222) — relevant to ja/ko/zh input, but hoster logins type Latin; document + revisit.
- **Windows:** unchanged (no CEF, no size change).
- Velopack: the Linux/macOS installers grow by the CEF payload; Windows unaffected. (Cross-platform Velopack packaging itself is a separate future concern — this design targets the sign-in feature, not the Linux/macOS *release* pipeline.)

## Build / csproj

Extend the existing multi-target head csproj (`net10.0-windows…;net10.0`):
- Add CefGlue.Avalonia + the ReactiveUI 11.3.9 pin as **non-Windows-only** PackageReferences.
- Add `CefGlueInteractiveAuthService`/`CefGlueLoginWindow` to the **non-Windows** compile; the current `UnsupportedInteractiveAuthService` becomes the fallback for any *other* non-Windows platform CEF can't serve (or is removed if CEF covers all).
- DI: `#else` branch registers `CefGlueInteractiveAuthService`.
- The CEF native payload + render subprocess must be copied to output for `linux-x64`/`osx-*` publishes.

## Reused (engine-agnostic — no changes)
`IInteractiveAuthService` + `InteractiveAuthSpec`/`InteractiveAuthResult` (Core); `WebViewLoginCapture` (SelectCookies/BuildCookieHeader/validator); `WebViewLoginProxy` (classification + SOCKS refusal); `WebViewLoginViewModel`; the service's gating/marshaling/owner/TCS pattern; `DialogOwnerResolver`.

## Testing

- **Agent-verifiable (headless, CI):** the pure helpers already have coverage and are reused. New headless tests: DI resolves `CefGlueInteractiveAuthService` on non-Windows; the JS-return shim (empty/null ⇒ not-complete, non-empty ⇒ complete) as a pure unit; the async delete-before-navigate sequencing as a unit around a fake cookie manager seam; SOCKS-refusal parity. **CEF itself is NOT initialized in headless CI** (heavy, needs a display) — the browser-driving paths are human-verified.
- **Native CEF smoke test (Linux/Xvfb, scripted + repeatable — the only test that exercises CEF for real):** boot CEF against a LOCAL test server and assert what unit tests cannot — two `CefRequestContext`s keep DISTINCT cookies, per-browser UA differs (header AND `navigator.userAgent`), delete-before-load ordering holds, the close-before-create race is handled, and close/reopen of the same cache path works. Catches wrong-context access, IO-thread affinity, and close ordering (the R1 items).
- **Human-only (on glibc Linux / macOS, mirroring the spike):** a real captcha sign-in per representative hoster → confirm Turnstile/reCAPTCHA solves and the HttpOnly session cookie / probe value is captured. Buzzheavier (Turnstile) is the proven exemplar.
- Windows suites (Core 1162 + head 459) must stay green — the change is additive on the non-Windows target.

## Accessibility (baseline — not a WCAG-conformance gate)

The login window is a modal wrapping a **third-party** page (the hoster's captcha login). Baseline we provide (cheap, mirrors the existing WebView2 window): a titled modal with an accessible name; **Escape/Cancel wired to `Close(null)`**; focus moved into the browser content on open and returned to the Cancel button on tab-out; focus contained to the dialog. **Out of scope** (engine-/third-party-owned, identical to the Windows WebView2 path, so not a parity or release gate for *this* feature): AT-SPI/VoiceOver semantic exposure of the embedded Chromium content and the captcha widget's own keyboard/audio alternatives — those belong to the hoster's page + CEF's a11y bridge, not to our dialog wrapper.

## Risks

1. **Stale Chromium-120** — passed Buzzheavier Turnstile in the spike, but may degrade as Cloudflare tightens; mitigate by upgrading to the CEF-134 CefGlue binding when it ships (staged, not yet released).
2. **Managed challenges (TakeFile)** — remain unsupported (as on Windows).
3. **cf_clearance JA3 gap** — unchanged; clearance-forwarding hosters stay fragile regardless of engine.
4. **macOS OSR CJK-IME crash** (Avalonia #14222) — document; Latin logins unaffected.
5. **Avalonia 11.3.18 × CefGlue-built-against-11.0.9** — worked for init + a full Buzzheavier flow in the spike; watch for event-ordering quirks; CefGlue tracks newer Avalonia (their commit "adapt to different Avalonia event order").
6. **CEF process-global settings** (UA, cache) vs per-window WebView2 — handled via per-`CefRequestContext` + resource-header UA rewrite; verify two concurrent hoster logins stay isolated.

## Open questions (for review)
- Sandbox: ship `--no-sandbox` always, or attempt the CEF sandbox on real desktops and fall back? (Spike used `--no-sandbox --disable-gpu`.)
- macOS: CEF app-bundle/helper packaging under Avalonia + Velopack — needs its own validation pass; in scope now or a follow-up?
- Do we keep `UnsupportedInteractiveAuthService` as a fallback for non-Win/Linux/macOS RIDs, or is CEF the sole non-Windows impl?
- **(RESOLVED, R1)** Serialize logins? → YES: a process-wide login gate to start; relax to the per-hoster gate only after a passing two-browser isolation test.
