# WebView2-in-NativeControlHost GO/NO-GO spike — verdict

**Date:** 2026-07-11
**Phase:** 2, Task 7 (the Phase 8 sign-in-architecture gate)
**Spike code:** `src/CSUploader.Avalonia/Spike/` (THROWAWAY — kept as the Phase 8 reference, deleted when Phase 8 lands the real login host)
**Driven via:** `scripts/ava-drive.cs` (AvaDevBridge, single-driver lock) + Win32 PowerShell probes. Live site `https://hitfile.net/login` (primary), `https://keep2share.cc/auth/login` (alternate for HttpOnly cookies).

---

## VERDICT: **GO — provisional on the maintainer confirming (a) native typing + (b) high-DPI**

Hosting a WebView2 `CoreWebView2Controller` in an Avalonia `NativeControlHost` child HWND works on Avalonia 11.3.18 / .NET 10 with `Microsoft.Web.WebView2` 1.0.4022.49. Every agent-verifiable point passed; **no point failed unfixably**, so the abort criterion (fall back to a separate WPF login-helper process) is **not** triggered. Two confirmations remain `NEEDS-the maintainer`, and they are not equivalent: **(a) typing into the login is the one interaction the agent could not exercise at all** — Windows refused the foreground grab a background agent needs to deliver native keystrokes — and (b) the 125%/150% DPI passes need a real display-scale change. Neither reflects a hosting defect, but (a) is genuinely *unexercised* (not merely un-signed-off), so the GO is **provisional** until the maintainer confirms both. Phase 8 proceeds with the in-process WebView2-in-Avalonia architecture on that basis.

### Load-bearing findings (these de-risk Phase 8)

1. **An `app.manifest` with the Windows `<supportedOS>` list is REQUIRED.** Without it, Avalonia's own `Win32NativeControlHost.DumbWindow` (the attachment surface the WebView is parented into) throws on creation *before* our code runs: *"Unable to create child window for native control host. Application manifest with supported OS list might be required."* The Task 1 Avalonia skeleton shipped without a manifest (WPF gets one implicitly). Added `src/CSUploader.Avalonia/app.manifest` (compatibility/supportedOS only; DPI stays programmatic via Avalonia's `Win32PlatformOptions`, matching the default Avalonia template). **Phase 8 depends on this file.**
2. **The WebView2 SDK drags in WPF's `WindowsBase` → a 0-warning-gate failure.** For any net5+ TFM the SDK references its `Microsoft.Web.WebView2.Wpf` and `.WinForms` wrapper assemblies unconditionally (no `UseWpf`/`UseWindowsForms` gate); the WPF wrapper pulls in `WindowsBase` 5.0 → an unresolvable `MSB3277` conflict against net10's `WindowsBase`. Fixed with a `_DropWebView2DesktopWrappers` target that removes the two desktop wrappers before RAR — the Avalonia head consumes only `Microsoft.Web.WebView2.Core`. `Core.dll` + the native `WebView2Loader.dll` still ship. Phase 8 keeps this target.
3. **Bounds-sync source of truth = host DIP × `RenderScaling`, NOT the child HWND's `GetClientRect`.** `GetClientRect` of the "static" child lags a resize (observed at 100% DPI: width tracked, height stayed stale at the pre-resize value), which would leave the WebView overflowing the host. The Avalonia control's laid-out `Bounds × RenderScaling` is authoritative and DPI-general. (This is exactly what the plan specified; the spike initially used `GetClientRect` as a "robustness" preference and had to back it out.)

---

## Per-point results

| # | Verify | Result | Evidence |
|---|--------|--------|----------|
| a | Keyboard/typing into a real hoster login incl. Turnstile | **PARTIAL — render/focus PASS; native typing UNEXERCISED (NEEDS-the maintainer)** | WebView renders the live login page and JS focus + `Probe` confirm the email field (`INPUT id=input-9 type=email`) takes focus with a readable (empty) value — but **typing into the login is the one interaction the agent could not exercise at all**: delivering native keystrokes needs the app foreground, which a background agent cannot grab (see below). Turnstile solving is likewise maintainer-only. |
| b | Bounds sync at 125%/150% DPI + resize | **PASS at 100% DPI (current) / NEEDS-the maintainer (125%, 150%)** | Resize sweep: host DIP 726×702 → 526×542 → 926×892; `controller.Bounds` == host DIP × 1.00 at every step; WebView `innerWidth`/`innerHeight` matched exactly; captures show the page filling the host with no dead zones at each size. |
| c | ShowDialog modal ownership | **PASS** | Modal-from-birth over the shown MainWindow. While open: `ava_windows` shows the dialog active + main inactive; **`IsWindowEnabled(ownerHWND) == False`** (native modality) while the modal stays enabled; after close the owner is re-enabled (`True`). NOTE: the managed automation layer (`ava_action` peers) BYPASSES native modality — a peer tab-select on the main window still took effect — so **real user input is blocked but automation is not; Phase 8 must not rely on peer-level input blocking.** |
| d | `Controller.Close()` releases the user-data-folder lock | **PASS** | Posted `WM_CLOSE` to the spike window; app process stayed alive; renaming `D:\temp2\cbuild-mig\webview2-udf` succeeded immediately — the lock was released by `Controller.Close()`, not by process exit. |
| e | CookieManager reads (incl. HttpOnly) | **PASS** | `CookieManager.GetCookiesAsync` on `keep2share.cc/auth/login` returned 4 cookies incl. **3 HttpOnly** (`accessToken`, `pcId`, `refreshToken`, all `httpOnly=True secure=True`) — the HttpOnly flag is visible, which is what the login capture rests on. (hitfile.net's login sets no HttpOnly cookie, so the alternate was used.) |

### Why (a) native typing and (b) 125%/150% are NEEDS-the maintainer, not FAIL

- **(a) native keystrokes:** delivering OS keystrokes to the WebView requires the app window to be the foreground window, and **a background agent cannot steal Windows foreground** — `SetForegroundWindow` (even with the `AttachThreadInput` trick) was refused (`GetForegroundWindow()` stayed on another window), so the accepted `SendInput` events went elsewhere and the field stayed empty. This is a limitation of *agent* verification, not of the hosting: WebView2 in a NativeControlHost is a standard pattern, and keystrokes route to the WebView when the window is foreground (always true when a real user types). The agent proved the WebView is live and interactive (renders, JS-focuses fields, reads `activeElement`, captures). **the maintainer's confirmation needed:** click the Email field and type; confirm characters land and Tab stays inside the page; solve the Turnstile challenge on a real login attempt (the challenge is visual/variable — "needs eyeballs"). **No credentials were entered by the agent.**
- **(b) higher DPI:** the current monitor is at 100% (`RenderScaling` 1.00). The bounds math is `host DIP × RenderScaling`, DPI-general by construction, and passed cleanly at 100%. The spike wires `ScalingChanged` → re-sync, so a live monitor-to-monitor DPI change re-fits the controller bounds without needing a layout pass. A background agent cannot change the system display scale. **the maintainer's confirmation needed:** run `…\ava\CSUploader.Avalonia.exe --agent --webview-spike` on a 125%/150% display (or drag it to a differently-scaled monitor), confirm the WebView still fills the host with no dead zones and corner links hit-test correctly.

---

## Evidence artifacts

WebView `CapturePreviewAsync` PNGs (the agent's eye inside the WebView — bridge screenshots can't see the native HWND):

- `D:\temp2\cbuild-mig\shots\spike-render-hitfile-login-726x702.png` — hitfile.net/login rendered inside the WebView (Email/Password/Login), fills the 726×702 host.
- `D:\temp2\cbuild-mig\shots\spike-resize-526x542-fills.png` — after resize to a 526×542 host: page reflowed, fills, no dead zone.
- `D:\temp2\cbuild-mig\shots\spike-resize-926x892-fills.png` — after resize to a 926×892 host: fills, footer now visible, no dead zone.

Diagnostics-panel readouts (bridge-readable Avalonia controls), captured during the run:
- StatusText: `NavigationCompleted (success=True, err=Unknown) → https://hitfile.net/login`
- BoundsText (each resize): `host DIP 926x892 | scaling 1.00 | controller.Bounds 926x892 | childRect(diag) 926x542`
- Cookies (keep2share): `accessToken httpOnly=True`, `pcId httpOnly=True`, `refreshToken httpOnly=True`, `HttpOnly count: 3`

---

## Phase 8 implications

- **Architecture: in-process WebView2 in an Avalonia `NativeControlHost`.** No separate WPF helper process. The real login host mirrors `WebViewLoginWindow.xaml.cs` (env creation, per-hoster user-data folder, cookie/probe capture, proxy/cert handling) but creates the controller via `env.CreateCoreWebView2ControllerAsync(hwnd)` instead of a WebView2 *control*, and manages bounds/`NotifyParentWindowPositionChanged`/`Close()` itself (see `Spike/WebView2SpikeWindow.axaml.cs`).
- **Keep:** the `app.manifest` (finding 1) and the `_DropWebView2DesktopWrappers` target (finding 2) — both are permanent, not throwaway.
- **Bounds:** drive `controller.Bounds` from host DIP × `RenderScaling` (finding 3); the child HWND `GetClientRect` is diagnostics-only. Sync on **layout changes, window moves, AND `TopLevel.ScalingChanged`** — a pure DPI change (dragging to a differently-scaled monitor, i.e. exactly the 125%/150% DPI test) changes `RenderScaling` with NO layout pass, so without the scaling-change hook the controller bounds go stale and the maintainer would see a false NO-GO. The spike wires all three.
- **Teardown:** `controller.Close()` in the window's `Closed`/HWND-destroy path releases the user-data-folder lock (verify d) — required so a re-opened login window against the same per-hoster folder doesn't hit "data directory already in use."
- **Cookies:** `CookieManager.GetCookiesAsync` surfaces HttpOnly cookies (verify e) — the whole XFS/moneyplatform capture mechanism carries over.
- **Open for the maintainer (before the Phase 8 GO is final):** native typing + one full real Turnstile sign-in (a); 125%/150% DPI pass (b).
