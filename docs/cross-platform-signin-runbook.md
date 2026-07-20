# Cross-platform interactive sign-in (Linux/macOS) — build & smoke runbook

The interactive captcha sign-in uses **WebView2 on Windows** and **CefGlue/CEF 120 on Linux/macOS**, behind one `IInteractiveAuthService`. This is the runbook for building, deploying, and smoke-testing the CEF path. Design: `docs/superpowers/specs/2026-07-20-cross-platform-signin-cefglue-design.md`.

## Build / publish (glibc Linux)

```
dotnet publish src/CSUploader/CSUploader.csproj -f net10.0 -c Release -r linux-x64 --self-contained false
```
Output carries `CefGlueBrowserProcess/` (incl. `libcef.so`, `.pak`s, `locales/`, the render subprocess) — keep that folder intact next to the app. **glibc only — Alpine/musl will not run CEF.**

## Runtime system libraries (Ubuntu/Debian)

.NET 10 runtime, plus the Chromium set **and the Avalonia X11 pair `libice6`/`libsm6`** (the non-obvious ones — without them Avalonia's X11 backend throws `DllNotFoundException: libICE.so.6` at startup):

```
sudo apt-get install -y dotnet-runtime-10.0 \
  libnss3 libnspr4 libgtk-3-0t64 libgbm1 libasound2t64 \
  libx11-6 libx11-xcb1 libxcb1 libxcomposite1 libxdamage1 libxext6 libxfixes3 \
  libxrandr2 libxkbcommon0 libxshmfence1 libdrm2 libcups2t64 libpango-1.0-0 libcairo2 \
  libatk1.0-0t64 libatk-bridge2.0-0t64 libexpat1 fonts-liberation ca-certificates \
  libice6 libsm6 libxi6 libxcursor1 libxrender1 libxinerama1 libxtst6 libfontconfig1 libfreetype6
```
(On Ubuntu 24.04+ the `t64`-suffixed names are correct; on Fedora/RHEL the equivalents are `nss gtk3 mesa-libgbm alsa-lib libX11 …`.)

**Sandbox:** the payload ships no `chrome-sandbox`, so the app runs sandbox-less — `CefBootstrap` sets the `no-sandbox` flag programmatically. On WSLg (no GPU passthrough) CEF logs benign GPU warnings and software-renders; no `--disable-gpu` is forced (GPU-init failure auto-falls-back).

## WSL note
Ubuntu on WSL2 (Windows 11) works via **WSLg** — the sign-in window renders on the Windows desktop. `wsl --install -d Ubuntu` (Alpine/musl does NOT work).

## Smoke checklist (the human acceptance gate — runtime correctness that compile/unit tests cannot cover)

Run the app on glibc Linux, trigger an account sign-in from Settings → Accounts for each shape, and confirm:

1. **Cookie hoster** (XFileSharing-family / ex-load): the login page + captcha render, you solve it, and the **HttpOnly session cookie is captured** (the account signs in). Verifies `VisitUrlCookies(includeHttpOnly:true)` on the login `CefRequestContext`.
2. **Probe hoster** (HitFile / Buzzheavier — Turnstile): sign-in completes and the **probe value** returns. Verifies `EvaluateJavaScript<string>` + the `WrapProbeScript` `return (…)` adaptation (CEF wraps evaluated code as a function body, so the shared IIFE probes need the prepended `return`).
3. **Proxy**: with a proxy configured for the account, the WebView routes through it (per-`CefRequestContext` proxy pref).
4. **Cancel / close**: cancel mid-captcha and close cleanly — no orphaned window, no hang (async teardown `CloseBrowser(true)`→`OnBeforeClose`).
5. **Two-context isolation** (unlocks relaxing the process-wide gate to per-hoster): two logins keep distinct cookies + UAs. Until this passes, the process-wide login gate stays (logins serialize) — this is deliberate.
6. **macOS** (fast-follow): the same, once the macOS app-bundle/helper packaging is validated.

## Known runtime unknowns to watch (from implementation review)
- DevTools `Network.setUserAgentOverride` actually changing `navigator.userAgent` (only used by the disabled TakeFile today).
- Windowed-vs-OSR focus: `_browser.Focus()` pushing keyboard focus into the CEF surface on open.
- Chromium-120 is ~2 years stale — Buzzheavier Turnstile passed in the spike, but a managed-challenge hoster (TakeFile) will still fail (as on WebView2). Upgrade to the CEF-134 CefGlue binding when it ships.
