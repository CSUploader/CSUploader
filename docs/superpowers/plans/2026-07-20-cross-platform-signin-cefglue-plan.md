# Cross-Platform Sign-In (CefGlue) — Implementation Plan

> **Design:** `docs/superpowers/specs/2026-07-20-cross-platform-signin-cefglue-design.md` (Codex-reviewed R1+R2). This plan implements it. Deep technical detail (threading contract, teardown sequence, gate ownership) lives in the design; tasks reference it rather than repeat it.

**Goal:** Linux/macOS interactive captcha sign-in via CefGlue/CEF behind the existing `IInteractiveAuthService` seam. Windows (WebView2) untouched.

**Branch:** `linux-port` (worktree `E:\Projects\CSUploader\CSUploader-linux`).

## Global constraints
- Windows build + suites (Core 1162 / head 459) MUST stay green — every change is additive on the **non-Windows** target only, conditioned in the multi-target head csproj (`net10.0-windows…;net10.0`).
- **New `Cef*` code (`Views/Cef/**`, `Services/Cef*.cs`) references CefGlue (non-Windows-only), so it MUST be excluded from the WINDOWS compile** — a `<Compile Remove>` / `<AvaloniaXaml Remove>` under `$(TargetFramework.Contains('windows'))` (the inverse of the existing WebView2-file exclusion). Keeping the new files under `Views/Cef/` + a `Services/Cef*` prefix lets one glob condition them. (Added in Task 1 so later tasks just drop files in.)
- **Platform scope this pass = Linux (`linux-x64`, glibc).** The code is cross-platform, but **macOS packaging** (app-bundle/helper/entitlements + the OSR CJK-IME caveat) is a documented **fast-follow**, not this pass — its own validation task once the Linux CEF path is proven.
- Build/test to a temp OutDir (`-p:OutDir=D:/temp2/…`). Portable target: `dotnet build -f net10.0`; Windows: `-f net10.0-windows10.0.17763.0`.
- No names in code/docs. Commit trailers as configured.
- CEF runtime behavior is **human-verified on glibc Linux/macOS** (the spike model) — headless CI cannot init CEF. Agent gates = compile (Windows + `-f net10.0` + `-r linux-x64` publish) + pure-logic unit tests + the design's required native Xvfb smoke.
- **Every task ends with a Codex read-only review of its diff before commit.**

---

### Task 1: Package wiring (non-Windows CefGlue refs)
**Files:** Modify `src/CSUploader/CSUploader.csproj`.
- [ ] Add, conditioned `!$(TargetFramework.Contains('windows'))`: `CefGlue.Avalonia` **120.6099.211** and `Avalonia.ReactiveUI` **11.3.9** (transitive pin; 11.3.18 fails restore).
- [ ] Add the **Windows-target exclusion** glob so the (yet-to-exist) Cef code never compiles on Windows: under `$(TargetFramework.Contains('windows'))`, `<Compile Remove="Views\Cef\**" />`, `<AvaloniaXaml Remove="Views\Cef\**" />`, `<Compile Remove="Services\Cef*.cs" />`. (Globs matching zero files are harmless until Tasks 4/5 add the files.)
- [ ] `dotnet build -f net10.0 -c Debug` → 0 warn/err (CEF native restores). `dotnet build -f net10.0-windows10.0.17763.0` → unchanged 0/0.
- [ ] Codex review → commit `feat(linux): add CefGlue package refs (non-Windows)`.

### Task 2: CEF bootstrap (non-Windows startup)
**Files:** `src/CSUploader/App.axaml.cs` (or a `#if !WINDOWS` partial `App.Cef.cs`).
- [ ] Under `#if !WINDOWS`: `CefRuntimeLoader.Initialize(new CefSettings{ RootCachePath=<per-app cache> })` inside `AppBuilder.…AfterSetup(...)` before lifetime start (spike-confirmed ordering); `CefRuntime.Shutdown()` on `ProcessExit`, guarded to run after browsers close (design §Lifecycle). Sandbox switches `--no-sandbox --disable-gpu` documented/default.
- [ ] Portable build compiles; Windows build unchanged; DI-smoke unaffected.
- [ ] Codex review → commit.

### Task 3: Pure-logic seams + unit tests (JS-return, delete-before-nav, cookie collection)
**Files:** Create `src/CSUploader/Views/Cef/CefProbeResult.cs` (or fold into the window) + `tests/CSUploader.Tests/…` (non-Windows-guarded tests where they touch CEF types; pure tests unguarded).
- [ ] **JS-return shim (test first):** a pure function `bool TryProbeComplete(string? evaluated, out string value)` — non-empty ⇒ complete (value = evaluated); empty/null ⇒ not complete. Test proves it does NOT re-run `TryParseJsonString` (CEF returns the raw value, not a JSON-quoted string). Design §mapping.
- [ ] **Cookie collection reuse:** verify `WebViewLoginCapture.SelectCookies`/`BuildCookieHeader` consume `(Name,Value)` tuples produced from `CefCookie` — a pure test feeding tuples (already engine-agnostic; add a guard test that the CEF projection order/empty-skip matches WebView2).
- [ ] **Delete-before-nav ordering:** a unit around a fake async cookie-manager seam proving `LoadURL` is not called until the delete callback completes.
- [ ] Full head suite green.
- [ ] Codex review → commit.

### Task 4: `CefGlueLoginWindow` (the browser + harvest)
**Files:** Create `src/CSUploader/Views/CefGlueLoginWindow.axaml(.cs)` (non-Windows compile). Reuse `WebViewLoginCapture`, `WebViewLoginProxy`, `WebViewLoginViewModel`.
- [ ] Host `AvaloniaCefBrowser` bound to a per-login `CefRequestContext` (own `CachePath`); proxy pref on that context; `OnCertificateError`→Continue when configured; UA via **DevTools `Network.setUserAgentOverride` + `OnBeforeResourceLoad` header** (design §UA); clear-stale-cookie = await context `DeleteCookies` before `LoadURL`; completion loop (1s timer + `LoadEnd`/`AddressChanged`); cookie path = context manager `VisitUrlCookies(includeHttpOnly:true)` IO-thread→TCS(`RunContinuationsAsynchronously`)→UI marshal, zero-result timeout; probe path = `EvaluateJavaScript<string>` via the Task-3 shim; capture full jar for `CookieCaptureUrl`; `_completed`/`_torndown` latches; **async teardown** `CloseBrowser(true)`→`OnBeforeClose`→release+dispose context; init-in-flight race guard (design §Lifecycle).
- [ ] `Close(InteractiveAuthResult?)`; Escape/Cancel→`Close(null)`; focus into browser on open, Cancel on tab-out (design §Accessibility).
- [ ] Portable build + `-r linux-x64` publish succeed; Windows unchanged.
- [ ] Codex review → commit.

### Task 5: `CefGlueInteractiveAuthService` + DI
**Files:** Create `src/CSUploader/Services/CefGlueInteractiveAuthService.cs` (non-Windows). Modify `src/CSUploader/App.axaml.cs` DI `#else` branch; retire/keep `UnsupportedInteractiveAuthService` as the non-Win/Linux/macOS fallback.
- [ ] Mirror `AvaloniaWebViewInteractiveAuthService`'s machinery: **process-wide login gate** (single `SemaphoreSlim(1,1)` for ALL logins to start — design §Concurrency), UI-thread marshal via `IUiDispatcher`, `DialogOwnerResolver.ResolveVisibleMainOnly()` owner, TCS over `ShowDialog<InteractiveAuthResult?>`, cancellation re-check, **SOCKS-with-auth pre-window refusal reused verbatim**; **gate held until the dialog/browser closes** (not on cancelled-caller return).
- [ ] `#else` registers `CefGlueInteractiveAuthService`.
- [ ] Head DI-smoke resolves it on non-Windows; Core 1162 + head 459 green; Windows build 0/0.
- [ ] Codex review → commit.

### Task 6: Native Xvfb CEF smoke test (required gate)
**Files:** `scripts/cef-smoke/…` (a small scripted harness + a local test server) + a runbook doc.
- [ ] Boot CEF against a LOCAL server and assert: two `CefRequestContext`s keep DISTINCT cookies; per-browser UA differs (header AND `navigator.userAgent`); delete-before-load ordering; close-before-create race handled; close/reopen same cache path. Runnable on Ubuntu-WSL (Xvfb or WSLg).
- [ ] Codex review → commit.

### Task 7: Packaging + Linux/macOS deps
**Files:** `src/CSUploader/CSUploader.csproj` (payload copy for `linux-x64`/`osx-*`), `docs/linux-macos-signin.md` (deps runbook).
- [ ] Ensure the CEF payload + `CefGlueBrowserProcess/` copy to the publish; document Linux system deps (Chromium set + **libice6/libsm6**) and `--no-sandbox --disable-gpu`; note macOS bundle/helper + the OSR CJK-IME caveat (Avalonia #14222).
- [ ] `dotnet publish -f net10.0 -c Release -r linux-x64` yields the payload; no WebView2 leak.
- [ ] Codex review → commit.

### Task 8: Human smoke (Linux + macOS) — the acceptance gate
Human-only (mirrors the spike): a real captcha sign-in for a representative cookie hoster (XFS/ex-load) AND a probe hoster (Buzzheavier/HitFile) on glibc Linux, then macOS — confirm captcha solves + the HttpOnly session cookie / probe value is captured, per-login isolation holds, cancel/close is clean. This is the release gate; the per-hoster (vs process-wide) gate is unlocked only after the 2-browser isolation check passes.

## Self-review
Spec coverage: engine/isolation → T1/T4; bootstrap → T2; cookie+JS+delete seams → T3/T4; service+gate+DI → T5; native gate → T6; packaging/deps → T7; human acceptance → T8. Every design §Lifecycle/Concurrency/UA/Accessibility item maps to a task. No placeholders — deep specifics deferred to the design by reference, not omitted.
