# Avalonia Migration Phase 9: Staged Cutover — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retire the WPF head and make the Avalonia head THE app — after a Fluent header-metrics polish, a per-grid parity sweep, the accumulated cross-head ledger fixes (applied to BOTH heads while WPF still exists), and the maintainer's manual E2E/sign-in smoke — then re-point the build/packaging at the Avalonia head under the same `AssemblyName`/`packId` `CSUploader`, and stage the first Avalonia release as a GitHub **prerelease** that installs verify update-in-place against before promotion.

**Architecture:** Strangler step 9 (final) of `docs/superpowers/specs/2026-07-10-avalonia-migration-design.md`, section Phases "Phase 9" (design lines 104-109), plus the accumulated cross-head ledger embedded in Phase 7's "CROSS-HEAD LEDGER (Phase 9)" (design line 102 item 5) and Phase 8's RECONCILED item 10 (design line 103). The Avalonia head is FEATURE-COMPLETE (Phase 8 verdict: Avalonia 448 / WPF+shared 1201 green). This phase touches Core + the WPF head in a bounded, deliberate way (the ledger fixes) — **the ONE phase where that is allowed** — then deletes the WPF head and re-targets the build, tests, and release pipeline.

**Tech Stack:** unchanged — .NET 10, Avalonia 11.3.18 + Avalonia.Controls.DataGrid 11.3.13 + Avalonia.Themes.Fluent 11.3.18 + Avalonia.Svg.Skia 11.3.0, `Microsoft.Web.WebView2` 1.0.4022.49 (Core wrapper only), Avalonia.Headless.XUnit 11.3.18, CommunityToolkit.Mvvm 8.4.2 (Core), Velopack (GithubSource, `prerelease:false`), Moq. **No packages added or removed this phase** (a CommunityToolkit.Mvvm-in-head audit is a note, not a removal — see Task 10). Bridge via `scripts/ava-drive.cs`; contact sheet via `scripts/contact-sheet.py`; fake data via `scripts/seed-fake-data.cs`; i18n gate via `scripts/md-to-resx.py --check`.

## Global Constraints

- Repo worktree: `E:\Projects\CSUploader\CSUploader-avalonia`, branch `avalonia-migration`, starting from tag `phase8-webview-login-ready` (tip `fde15c9`). **NEVER touch `E:\Projects\CSUploader\CSUploader`** (the maintainer's main tree — has uncommitted Buzzheavier work). HARNESS: PowerShell's default cwd is the MAIN tree — ALWAYS use absolute worktree paths for build/test; use PowerShell (not Bash) for `-p:OutDir=D:\...` builds (Bash strips the backslashes → a bridge drive launches a STALE exe). Git operations use `git -C E:\Projects\CSUploader\CSUploader-avalonia ...`.
- **PHASE 9 IS THE ONE PHASE THAT MAY TOUCH `src/CSUploader.Core/` AND THE WPF HEAD (`src/` outside the two nested projects).** Tasks 2-5 (the cross-head ledger fixes) modify Core and/or the WPF head deliberately, so the WPF head stays a live, byte-comparable regression net through the maintainer's manual smoke (Task 7). Every such edit is called out in its task and is scrutinised in the final whole-diff review (Task 12). Do NOT make any Core/WPF-head change outside the four named ledger fixes and the cutover deletion.
- Suite gate after every code task (definition of done). Separate OutDirs are mandatory (a shared OutDir mixes WPF and Avalonia assemblies and breaks discovery); never run bare solution-level `dotnet test -p:OutDir=...`:
  - WPF+shared: `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests` — **1201 green** at phase start (Phase 8 verdict). Confirm the exact number at Task 1's gate and correct it here if it drifted. Cross-head fixes (Tasks 2-5) that add WPF/Core tests RAISE this — record each new baseline and carry it forward.
  - Avalonia: `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests` — **448 green** at phase start (Phase 8 verdict). Every task that adds Avalonia tests raises it — record and carry forward.
  - **POST-DELETION BASELINE (Task 11):** once the WPF head is deleted, `tests/CSUploader.Tests.csproj` re-targets to `CSUploader.Core` and sheds every WPF-head-coupled test. The exact green count is DISCOVERED at Task 11 (it equals 1201 + the Core/WPF-side tests added by Tasks 2-5 − the deleted WPF-only tests); Task 11 records it as the new permanent WPF+shared→Core baseline. The Avalonia suite is unchanged by deletion and becomes the primary UI suite. **State the emergent number in the Task 11 gate; do not leave it as "≈".**
- Head builds (0-warning gate, forced rebuild): Avalonia `dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -t:Rebuild -p:OutDir=D:\temp2\cbuild-mig\ava` AND `-c Release -t:Rebuild`; WPF (until deletion) `dotnet build src/CSUploader.csproj -c Debug -t:Rebuild -p:OutDir=D:\temp2\cbuild-mig\wpf`. Both heads MUST build 0-warning in Debug AND Release. Scratch DBs live beside those exes; seed with `dotnet run scripts/seed-fake-data.cs -- <outdir>` (idempotent, synthesized bogus data only).
- Every csproj keeps `LangVersion=preview`, `Nullable=enable`, `ImplicitUsings=enable`, TFM `net10.0-windows10.0.17763.0`, `EnableWindowsTargeting=true`. Version pins are hard; do not bump package versions.
- **Velopack continuity (design line 20):** packId `CSUploader`, mainExe `CSUploader.exe`, implicit `win` channel, GithubSource. After cutover the Avalonia head MUST publish with `AssemblyName=CSUploader` and the same packId or update-in-place breaks. `UpdateService` constructs `GithubSource(..., prerelease: false)` (`src/CSUploader.Core/Lib/Update/UpdateService.cs:22`) — verified — so a GitHub release flagged **prerelease** is invisible to installed apps until promoted.
- **i18n permanent gate:** `Strings*.resx` are GENERATED from `docs/i18n-inventory*.md` via `scripts/md-to-resx.py`; the resx live in `src/CSUploader.Core/Resources/`. NEVER hand-edit or hand-merge resx. **This phase adds ZERO new i18n keys** (every string the ledger fixes and cutover need already exists and is live). `scripts/md-to-resx.py --check` over all six languages must stay green (it is also wired as the `tests/Localization/I18nRegenGateTests.cs` test — framework-free, survives cutover). Deletion (Task 9) does not touch any resx.
- **AGENT-SAFETY (unchanged, load-bearing):** NO task may drive a real hoster login or upload. The live Avalonia head is launched ONLY with `--agent` (forces `AutostartUploads=Never` + scheduler PauseAll), against a per-bin SCRATCH DB (never a real `CSUploader.db`), seeded with bogus credentials. The wizard is driven up to but NEVER through the final start action (anonymous pipelines would really upload). **DO-NOT-CLICK bridge surfaces (design line 103 item 10):** EditAccount **"Sign in…"** (`EditAccount_SignInButton`, un-credentialed real-WebView login-page GET), **"Check account"**, and **"Refresh all accounts"** (both POST the seeded FAKE creds — `fake_rg_user`/`fake_catbox_user` — to REAL Rapidgator/Catbox endpoints via the live pipelines). The parity sweep (Task 6) inspects these controls' STRUCTURE but never invokes them. Native typing/focus into the WebView, the Turnstile challenge, and 125/150% DPI are agent-UNVERIFIABLE (Windows refuses a background agent the foreground grab) → maintainer-only (Task 7).
- `[AvaloniaFact]` discipline (Phase 3 rule): tests that open windows or mutate a process-global static restore that state in `finally`; close every window opened (snapshot the window list before closing). Pure-logic tests use plain `[Fact]`.
- Shots convention: `D:\temp2\cbuild-mig\shots\<view>-<light|dark>-<wpf|ava>.png`. Defender-ML false-positive (Phase 5/6): demo/gallery/test code carries NO dense hoster-URL literals — use `about:blank` / `https://example.test/` placeholders only.
- Port rules 1-49 (Phases 4-8) are carried forward by reference. This phase ADDS no port rule (it is cutover, not porting); the header-metrics lever is the `DataGridSortIconMinWidth` resource override + header `Padding` trim (design line 102 item 10 / line 101 reconcile item 10).
- Commits end with the trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

## DECISIONS for the team lead (surface — do NOT assume answers)

These are open questions the plan deliberately does NOT resolve. Each is referenced from the task that would act on the answer. Route to the maintainer via the team lead before the task that depends on it.

**RESOLVED (the maintainer, 2026-07-15):**
1. **Message-box icons → ADD** to match WPF (info/warning/error/question per type). New task #19, Avalonia-head-only, reviewer-gated, runs in parallel with the T7 smoke.
2. **ProgressWindow → DELETE** (team-lead recommendation; the maintainer not objecting) at cutover cleanup — the dead generic window + its Gallery demo; `UpdateProgressWindow` (the real progress surface) stays.
3. **PasswordChar masking → KEEP masked.** Signed off as an accepted divergence from WPF cleartext (also keeps secrets out of `ava_props` reads).
4. **Phase 1 merge / Buzzheavier → single final branch→master integration CONFIRMED; Buzzheavier reconciled BEFORE cutover.** the maintainer commits Buzzheavier to master (his flow); the team lead then merges master into the branch + manually re-homes the non-auto-mapping bits (PNG→avares `ImageResources`; EditAccount hoster-list→Core `ApiKeyHosters`/`SessionCookieHosters`) per the breakdown below. **Cutover (T8+) is PAUSED on Buzzheavier landing AND the T7 smoke.** — STATUS 2026-07-15: Buzzheavier committed to master (`a2b88eb`), merged onto the branch at **`13b2b8c`**, and 6-lens adversarially verified (all sound, 0 defects: conflict-resolution fidelity / both-head DI / nothing-dropped / i18n-regen-safety / drift-guard / build-test-scope; shared/Core **1228**, Avalonia **454**, both heads 0-warning Debug+Release). **Cutover now paused ONLY on the T7 manual smoke.** — T7 SMOKE PASSED 2026-07-15 (the maintainer verified a real upload + a Turnstile WebView sign-in; DPI check deferred to the prerelease-verify window — cheap insurance, not a code gate). the maintainer AUTHORIZED the FULL cutover incl. WPF-head deletion (design plan; `last-wpf` tag is the rollback). Cutover T8–T13 GREENLIT.
5. **Info-toast Uploaded-tab-flip → KEEP** (accepted divergence; the maintainer not objecting).
6. **Core `--agent` network latch → SKIP.** The DO-NOT-CLICK operating rule (Sign in / Check account / Refresh all accounts) remains the sole guard; no Core latch task. Less surface touched pre-cutover.
Minors: csproj filename → keep (default); CommunityToolkit.Mvvm in head → keep (default).

1. **Message-box system icons.** The Avalonia `MessageBoxWindow` (Phase 4) renders text + buttons but no Windows system icon glyph (info/warning/error) that `MessageBox.Show` showed on WPF. Accept the divergence, or add glyphs before cutover? (Affects nothing structural; a Task-6 sweep observation, not a blocker.)
2. **ProgressWindow keep-vs-delete.** The Avalonia generic `ProgressWindow` (`src/CSUploader.Avalonia/Views/ProgressWindow.axaml`, distinct from `UpdateProgressWindow`) has NO production consumer — only the DEBUG `GalleryWindow` demo (`GalleryWindow.axaml.cs:386`) instantiates it. Keep as a demoed dialog, or delete it (and its gallery demo) at cutover?
3. **PasswordChar masking sign-off.** The EditAccount/EditProxy Password/ApiKey/SessionCookie boxes use `PasswordChar` masking (Phase 5 recorded deviation from WPF's cleartext) so `ava_props` can't read secrets off `TextBox.Text`. Sign off the deviation as shipped?
4. **Phase 1 merge-to-master STATUS + Buzzheavier reconciliation (not a question — a finding + a planning need).** The design's Merge protocol (design line 114) says Phase 1 merges back to master once reviewed. **It never happened:** `master`'s tip is `d14147e` and equals the merge-base with `avalonia-migration` — master has no Core-split/purification commit; the ENTIRE migration lives only on the branch. Consequence: the cutover brings everything to master in ONE eventual branch→master integration (finishing-a-development-branch), not incrementally. Confirm the team lead is content with a single final integration (vs a retroactive Phase 1 merge-back now).
   **Buzzheavier reconciliation (design Merge protocol, lines 113-116):** the maintainer's in-flight Buzzheavier work is UNCOMMITTED in the MAIN tree (`E:\Projects\CSUploader\CSUploader`) and was authored against the OLD pre-split layout — it edits WPF-head files that Task 9 DELETES (`src/Views/EditAccountWindow.xaml.cs`, `src/App.xaml.cs`, `src/Resources/ImageResources.xaml`) and adds a pipeline + a PNG. After cutover it re-homes as follows, and this must be planned DELIBERATELY (re-applying old-layout WPF edits after the WPF head is gone is materially harder than a normal `git merge master` — rename detection maps nothing, and the target files no longer exist):
   - `BuzzheavierPipeline.cs` → `src/CSUploader.Core/Upload/Pipeline/Hosters/` (Core); its `FileHosterClient`/`IFileHosterPipeline` edits → the Core copies; the DI wire → `ServiceRegistration.AddCoreServices`.
   - i18n strings → via `docs/i18n-inventory*.md` → `scripts/md-to-resx.py` regen (NEVER hand-merge resx).
   - The `EditAccountWindow.xaml.cs` hoster-list edit → the **Phase-5-hoisted-to-Core** `ApiKeyHosters`/`SessionCookieHosters` (design line 100 prep item 4), NOT the deleted WPF code-behind.
   - The PNG logo → a KEYED Avalonia `ImageResources`/`avares://` entry that rename detection does NOT produce (design line 116 — the mandatory manual-add checklist item on every master merge); the WPF `src/Resources/ImageResources.xaml` edit is MOOTED by deletion.
   - The WPF-head `App.xaml.cs`/`EditAccountWindow.xaml.cs`/`ImageResources.xaml` edits are otherwise mooted by Task 9's deletion.
   Decide WHEN to reconcile: fold Buzzheavier into the branch BEFORE cutover (author it against Core paths while both heads still exist — easiest), or re-home it onto the Avalonia-only tree AFTER cutover (harder). Recommend before-cutover if Buzzheavier is landing regardless.
5. **Info-toast Uploaded-tab-flip divergence.** The tray/completion info-toast body click restores the window AND flips to the Uploaded tab (WPF's balloon click is inert) — design line 102 reconcile item 6 lists it as an accepted divergence. Confirm it stays.
6. **Core-level `--agent` outbound-network latch.** A deliberate fail-fast/no-op on real-hoster contact under `--agent` (belt-and-braces over the existing DO-NOT-CLICK discipline). Out of scope for Phase 8 (Core was frozen). Phase 9 CAN touch Core — add the latch now, or leave the DO-NOT-CLICK operating rule as the sole guard? If "add", it becomes an additional task; the plan does NOT include it pending this decision.

Minor (fold into the named task if unanswered — defaults noted):
- **csproj filename rename** (`CSUploader.Avalonia.csproj` → `CSUploader.csproj`): cosmetic; the exe/pack identity come from `AssemblyName`, not the filename. DEFAULT: keep the filename (minimise ProjectReference churn) — Task 10 note.
- **CommunityToolkit.Mvvm in the head:** the head package ref is consumed by `GalleryWindow` (`using CommunityToolkit.Mvvm.Input;`, DEBUG-only). DEFAULT: keep it (dropping needs the gallery gone too) — Task 10 note.

## Scope coverage (design lines 104-109 + ledger)

| # | Requirement | Task |
|---|-------------|------|
| Pre-sweep | One-shot Fluent header-metrics pass (`DataGridSortIconMinWidth` override + header Padding trim; PRIORITIZE blank checkbox headers — proxy 'On' 40px, accounts '✓' 30px) — design line 102 item 5 / line 101 item 10 | Task 1 |
| 104.1 | Parity sweep (menus, context menus, key bindings, per-grid interaction checklist incl. "Delete inside a cell editor edits text" + "Ctrl+C AND Ctrl+Insert both copy") — design line 102 item 6 | Task 6 |
| Ledger (a) | Ask→Minimize-without-Remember strands the app hidden with no tray icon → `ITrayIconService.EnsureIconForSession()` on BOTH heads; do NOT mutate in-memory `CloseAction` | Task 2 |
| Ledger (b) | `MainViewModel.InitializeAsync` idempotency guard (Core) | Task 3 |
| Ledger (c) | `MainViewModel` IDisposable (stop the 6h `_updateTimer`; unsubscribe the ctor `Localizer.Instance.PropertyChanged` lambda) | Task 4 |
| Ledger (d) | Register `UploadWizardViewModel` in DI (Phase 6 hand-builds it via `App.Services`) | Task 5 |
| 104.2 | the maintainer's manual smoke (E2E upload + one WebView sign-in incl. Turnstile, Tab-out/in native focus, 125/150% DPI, Win10 DWM attr-19) WHILE the WPF head still exists | Task 7 |
| 104.4 | Tag last WPF commit `last-wpf` — BEFORE deletion | Task 8 |
| 104.3 | Delete WPF head | Task 9 |
| 104.3 | Avalonia head takes `AssemblyName`/`packId` CSUploader; `release.yml` re-pointed; `app.manifest` assemblyIdentity name→CSUploader | Task 10 |
| 104.3 | Retarget test project; delete WPF-coupled tests; i18n `--check` green; record post-deletion baseline | Task 11 |
| 104.3 | README/docs; design reconcile; final whole-diff review | Task 12 |
| 104.5 | Stage release as a GitHub **prerelease**; verify update-in-place from a real install; promote to full (maintainer-driven) | Task 13 |

## Agent-verifiable vs maintainer-only split

- **Agent-verifiable (bridge `--agent` + scratch DB + seed + `about:blank`, headless tests, build/test/git/docs):** Task 1 (headless resource test + contact-sheet re-shoot of the four grids), Tasks 2-5 (headless/unit tests + 0-warning rebuild both heads), Task 6 **agent subset** (per-grid right-click targeting, empty-click clear, Delete key, Delete-inside-editor-edits-text guard, Enter/double-click open, auto-scroll, Ctrl+C AND Ctrl+Insert copy, menu/context-menu STRUCTURE, key bindings, settings persistence via scratch-DB relaunch — all against seeded fake data, never invoking the DO-NOT-CLICK surfaces), Tasks 8-12 (git/build/test/docs — mechanical).
- **maintainer-only (hard human gates):** Task 7 in full (E2E real upload, real WebView sign-in + Turnstile typing, native Tab-out/Tab-in focus, 125/150% DPI WebView bounds + toast placement, Win10 DWM dark-title-bar attr-19 visual on a Win10 box), the DO-NOT-CLICK real-hoster actions noted inside Task 6, and Task 13's verify-update-in-place + prerelease→full promotion (the maintainer's gh has repo access; the session's local gh 404s the private repo — never `gh release create` locally).

---

## Task 1: Pre-sweep Fluent header-metrics pass

Reclaim the dead horizontal reserve in narrow DataGrid column headers so blank checkbox headers stop clipping. Design lever (Phase 6 Task 6 decompile, design line 101 reconcile item 10): the Fluent `DataGridColumnHeader` template reserves a static `DataGridSortIconMinWidth` (default 32px) for the sort glyph EVEN when no glyph shows; this — plus header `Padding` — pushes short header content out of view at <44px. PRIORITIZE the checkbox columns that go FULLY blank: the proxies grid `On` header (40px) and the accounts `✓` header (30px). This is a ONE-SHOT resource/style pass, not per-view; it runs BEFORE the parity sweep so Task 6 judges the polished grids.

**Files:**
- Modify: `src/CSUploader.Avalonia/Resources/Tokens.axaml` (add the `DataGridSortIconMinWidth` override resource)
- Modify: `src/CSUploader.Avalonia/Resources/BaseStyles.axaml` (add a `DataGridColumnHeader` Padding-trim style)
- Test: `tests/CSUploader.Avalonia.Tests/Resources/HeaderMetricsTests.cs` (create)

**Interfaces:**
- Produces: an application-scoped resource `DataGridSortIconMinWidth` (double) and a global `DataGridColumnHeader` style; consumed only by the Fluent DataGrid theme at render time. No public C# surface.

- [ ] **Step 0: Confirm the exact resource key.** The lever hinges on the resource NAME in Avalonia.Controls.DataGrid 11.3.13. Confirm it against the packaged theme before writing the override:

Run (PowerShell, worktree-absolute): open `avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml` from the restored package, or decompile with the dotnet-skills `ilspy-decompile` flow, and grep for `SortIcon` / `MinWidth` inside the `DataGridColumnHeader` `ControlTheme`.
Expected: a keyed `x:Double` named `DataGridSortIconMinWidth` (design-recorded default `32`). If the key differs in 11.3.13, use the ACTUAL key name in Steps 1/3 and note the correction in the commit body. Do not proceed on the assumed name without this confirmation.

- [ ] **Step 1: Write the failing headless test.** Create `tests/CSUploader.Avalonia.Tests/Resources/HeaderMetricsTests.cs`:

```csharp
// <copyright file="HeaderMetricsTests.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Avalonia;

namespace CSUploader.Tests.Avalonia.Resources;

/// <summary>
/// The one-shot Fluent header-metrics pass (Phase 9 Task 1): the app overrides the DataGrid theme's
/// sort-icon reserve so narrow checkbox-column headers (proxies 'On', accounts '✓') stop clipping to blank.
/// This guards the resource override's presence + value; the visual proof is the Task 1 contact-sheet re-shoot.
/// </summary>
public class HeaderMetricsTests
{
    [Fact]
    public void App_OverridesDataGridSortIconMinWidth()
    {
        Assert.True(
            Application.Current!.TryGetResource("DataGridSortIconMinWidth", null, out object? value),
            "DataGridSortIconMinWidth override missing — the header-metrics reclaim regressed.");
        Assert.Equal(16d, Assert.IsType<double>(value));
    }
}
```

- [ ] **Step 2: Run it — verify it fails.**

Run: `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests --filter "FullyQualifiedName~HeaderMetricsTests"`
Expected: FAIL — either the resource is absent (TryGetResource false) or resolves to the Fluent default `32`.

- [ ] **Step 3: Add the override resource.** In `src/CSUploader.Avalonia/Resources/Tokens.axaml`, add inside the root `ResourceDictionary` (use the key confirmed in Step 0; `16` reclaims 16px of the 32px reserve while leaving glyph room on sorted columns — a starting value, tuned to the contact sheet in Step 6):

```xml
<!-- Phase 9 header-metrics reclaim (design line 101 reconcile item 10): the Fluent DataGridColumnHeader
     template statically reserves DataGridSortIconMinWidth (32px) for the sort glyph even when none shows,
     clipping short headers to blank at <44px (proxies 'On' 40px, accounts '✓' 30px worst). Halving the
     reserve reclaims the space; sorted columns still show their glyph. App-scoped so it wins over the theme. -->
<x:Double x:Key="DataGridSortIconMinWidth">16</x:Double>
```

- [ ] **Step 4: Trim the header padding.** In `src/CSUploader.Avalonia/Resources/BaseStyles.axaml`, add a global `DataGridColumnHeader` style (starting value `4,0` — tuned in Step 6):

```xml
<!-- Phase 9 header-metrics reclaim: trim the column-header padding alongside the sort-icon reserve so short
     checkbox headers ('On', '✓') render fully. Global (one-shot pass); tuned against the Task 1 contact sheet. -->
<Style Selector="DataGridColumnHeader">
  <Setter Property="Padding" Value="4,0" />
</Style>
```

- [ ] **Step 5: Run the test — verify it passes.**

Run: `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests --filter "FullyQualifiedName~HeaderMetricsTests"`
Expected: PASS.

- [ ] **Step 6: Re-shoot the four grids into the contact sheet and eyeball the checkbox headers.** Build the seeded Avalonia head, launch `--agent`, screenshot UploadsView + UploadedView + SettingsView (both grids) + LogsView, light+dark, into `D:\temp2\cbuild-mig\shots\`, and rebuild the contact sheet:

Run (PowerShell): `dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\ava` then `dotnet run scripts/seed-fake-data.cs -- D:\temp2\cbuild-mig\ava` then drive `scripts/ava-drive.cs` to launch `D:\temp2\cbuild-mig\ava\CSUploader.Avalonia.exe --agent` and `ava_screenshot` each view (explicit `maxWidth 2500` for the wide grids), then `python scripts/contact-sheet.py`.
Expected: the proxies `On` header and accounts `✓` header render their content (not blank); no sortable header's glyph is crowded. If either still clips or a glyph crowds, adjust the `16`/`4,0` values (both the test's `Equal(16d, …)` and the resource must agree) and repeat.
**HARD GATE (do NOT skip — the unit test is insufficient on its own):** `HeaderMetricsTests` proves the resource is PRESENT, not that the Fluent DataGrid template CONSUMES that key. If Step 0 mis-identified the key, the unit test passes GREEN while the override no-ops and the headers stay blank. This contact-sheet eyeball is the ONLY check that the reclaim actually took visual effect — Task 1 is NOT done until the proxies `On` and accounts `✓` headers are confirmed non-blank in the shots. If they are still blank with the unit test green, the key is wrong: return to Step 0.

- [ ] **Step 7: Full Avalonia suite green + 0-warning rebuild.**

Run: `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests` and `dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -t:Rebuild -p:OutDir=D:\temp2\cbuild-mig\ava` and `-c Release -t:Rebuild`.
Expected: 449 green (448 + the new test), 0 warnings Debug + Release.

- [ ] **Step 8: Commit.**

```bash
git -C E:\Projects\CSUploader\CSUploader-avalonia add src/CSUploader.Avalonia/Resources/Tokens.axaml src/CSUploader.Avalonia/Resources/BaseStyles.axaml tests/CSUploader.Avalonia.Tests/Resources/HeaderMetricsTests.cs
git -C E:\Projects\CSUploader\CSUploader-avalonia commit -m "Phase 9: reclaim DataGrid header sort-icon reserve so narrow checkbox headers stop clipping"
```

---

## Task 2: Cross-head ledger fix (a) — `ITrayIconService.EnsureIconForSession()` on BOTH heads

**BUG (byte-identical on both heads):** Close-action `Ask` → user picks *Minimize* but leaves *Remember* UNTICKED. The choice hides the window but does NOT persist `CloseAction=MinimizeToTray`, so `UpdateVisibility()` (which gates the icon on `MinimizeToTray || CloseAction==MinimizeToTray`) tears the icon down — the app is now hidden with no tray icon and no window: **stranded.** WPF: `MainWindow.xaml.cs:95-100` (Ask→Minimize branch calls `UpdateVisibility()`). Avalonia: `MainWindow.axaml.cs:147-153` (`ApplyCloseActionChoiceAsync` Minimize branch calls `UpdateVisibility()`). Fix (design line 102 item 5): a new `EnsureIconForSession()` that forces the icon on for the rest of this session regardless of settings — **do NOT silently mutate in-memory `CloseAction`.** Applied to BOTH heads while WPF exists.

**Files:**
- Modify: `src/CSUploader.Core/Services/ITrayIconService.cs` (add the member)
- Modify: `src/Services/TrayIconManager.cs` (WPF impl)
- Modify: `src/CSUploader.Avalonia/Services/AvaloniaTrayIconService.cs` (Avalonia impl)
- Modify: `src/Views/MainWindow.xaml.cs:99` (WPF close handler — Ask→Minimize branch)
- Modify: `src/CSUploader.Avalonia/Views/MainWindow.axaml.cs:151` (Avalonia — `ApplyCloseActionChoiceAsync` Minimize branch)
- Test: `tests/CSUploader.Avalonia.Tests/Services/AvaloniaTrayIconServiceTests.cs` (extend — Avalonia impl)
- Test: `tests/CSUploader.Avalonia.Tests/Views/MainWindowCloseToTrayTests.cs` (**UPDATE the existing** `AskMinimize_NoRemember_HidesWithoutBalloon_AndDoesNotPersist` test — its `:169` `UpdateVisibility` assert now contradicts the fixed branch; do NOT add a duplicate test for the same scenario)
- Test (WPF-side, transient — deleted at Task 11; PERMITTED to drop if not headless-runnable — see Step 8): `tests/Services/TrayIconManagerEnsureIconTests.cs` (create)

**Interfaces:**
- Produces: `void ITrayIconService.EnsureIconForSession()` — implemented by both heads; forces the icon present for the session, no-op after Dispose. Consumed by both `MainWindow` close handlers on the Ask→Minimize branch.

- [ ] **Step 1: Write the failing Avalonia impl test.** Extend `tests/CSUploader.Avalonia.Tests/Services/AvaloniaTrayIconServiceTests.cs` with a test proving `EnsureIconForSession()` creates an icon even when settings say don't-minimize (mirror the existing UpdateVisibility test's harness — same `AppSettings`/logger/toast fakes; assert via whatever the file already uses to observe icon presence, e.g. a second `UpdateVisibility()`-then-dispose parity or the service's own state seam). If the existing tests observe icon presence only indirectly, add a minimal internal `bool HasIcon => _trayIcon is not null;` seam to `AvaloniaTrayIconService` (InternalsVisibleTo already set) and assert on it:

```csharp
[AvaloniaFact]
public void EnsureIconForSession_CreatesIcon_EvenWhenSettingsSayNoMinimize()
{
    AppSettings settings = new() { MinimizeToTray = false, CloseAction = CloseAction.Ask };
    AvaloniaTrayIconService tray = new(settings, Mock.Of<IAppLogger>(), Mock.Of<IToastNotificationService>());
    try
    {
        tray.UpdateVisibility();           // settings say no icon…
        Assert.False(tray.HasIcon);
        tray.EnsureIconForSession();        // …but the session-force creates it anyway.
        Assert.True(tray.HasIcon);
    }
    finally
    {
        tray.Dispose();
    }
}
```

- [ ] **Step 2: Run it — verify it fails to compile** (`EnsureIconForSession`/`HasIcon` undefined).

Run: `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests --filter "FullyQualifiedName~EnsureIconForSession"`
Expected: BUILD FAILURE (member not defined).

- [ ] **Step 3: Add the interface member.** In `src/CSUploader.Core/Services/ITrayIconService.cs`, after `NotifyHidden`:

```csharp
    /// <summary>
    /// Forces the tray icon to exist for the remainder of this session, regardless of the persisted
    /// <see cref="AppSettings.MinimizeToTray"/>/<see cref="AppSettings.CloseAction"/> settings. Used by the
    /// close-action "Minimize" choice when the user did NOT tick "Remember": the window hides but
    /// <see cref="UpdateVisibility"/> would otherwise tear the icon down (settings still say don't-minimize),
    /// stranding the app hidden with no icon. Honours the one-off choice WITHOUT mutating in-memory settings.
    /// </summary>
    void EnsureIconForSession();
```

- [ ] **Step 4: Implement both heads.** In `src/CSUploader.Avalonia/Services/AvaloniaTrayIconService.cs`, add the `HasIcon` seam (if not already present) and the method; in `src/Services/TrayIconManager.cs`, add the same method:

Avalonia (`AvaloniaTrayIconService.cs`, near `EnsureIcon`):
```csharp
    // Test seam (InternalsVisibleTo -> CSUploader.Avalonia.Tests): icon presence, for the EnsureIconForSession
    // strand-fix test. Behaviourally identical to inlining the null-check at the call site.
    internal bool HasIcon => _trayIcon is not null;

    /// <inheritdoc />
    public void EnsureIconForSession()
    {
        if (_disposed)
        {
            return;
        }

        EnsureIcon();
    }
```

WPF (`TrayIconManager.cs`, near `EnsureIcon`):
```csharp
    /// <inheritdoc />
    public void EnsureIconForSession()
    {
        if (_disposed)
        {
            return;
        }

        EnsureIcon();
    }
```

- [ ] **Step 5: Route both close handlers to the new method.** In `src/CSUploader.Avalonia/Views/MainWindow.axaml.cs`, in `ApplyCloseActionChoiceAsync` the `MinimizeToTray` branch, replace `_tray?.UpdateVisibility();` (line 151) with `_tray?.EnsureIconForSession();`. In `src/Views/MainWindow.xaml.cs`, in `MainWindow_Closing` the Ask→Minimize sub-branch, replace `_trayIconManager.UpdateVisibility();` (line 99) with `_trayIconManager.EnsureIconForSession();`. Leave the DIRECT `CloseAction.MinimizeToTray` branches and the minimize-StateChanged paths on both heads untouched (those run only when settings already command the icon, so `UpdateVisibility()` is correct there).

- [ ] **Step 6: UPDATE the existing close-handler test that now encodes the OLD contract.** After Step 5 the existing `AskMinimize_NoRemember_HidesWithoutBalloon_AndDoesNotPersist` (`tests/CSUploader.Avalonia.Tests/Views/MainWindowCloseToTrayTests.cs:150-180`) is RED — its `:169` `tray.Verify(t => t.UpdateVisibility(), Times.Once)` contradicts the fixed branch (the Ask→Minimize path now calls `EnsureIconForSession`, not `UpdateVisibility`). This IS the strand-fix's regression test — update it in place; do NOT add a second test for the same scenario. Change the `:169` line to verify the new contract and add the negative assert, keeping every other assert (hidden, NO balloon, CloseAction unchanged, not persisted) exactly as-is:

```csharp
            Assert.False(w.IsVisible);                                 // hidden
            tray.Verify(t => t.EnsureIconForSession(), Times.Once);    // session-forced icon (strand fix)
            tray.Verify(t => t.UpdateVisibility(), Times.Never);       // NOT the settings-gated refresh (would strand)
            tray.Verify(t => t.NotifyHidden(), Times.Never);           // NO balloon on the Ask->Minimize branch
            Assert.Equal(CloseAction.Ask, settings.CloseAction);       // unchanged (Remember = false) — NOT mutated in memory
            Assert.Null(await repo.FindByKeyAsync(SettingKey.CloseAction)); // not persisted
```
Update the test's comment header to say it now asserts the session-forced icon (the strand fix), not the plain refresh. The direct-`MinimizeToTray` tests (`:36`, `:93`) and the `AskExit`/`AskCancelled` tests exercise untouched branches — leave them.

- [ ] **Step 7: Write the WPF-side impl test — OR document why it can't run headlessly and drop it.** Attempt `tests/Services/TrayIconManagerEnsureIconTests.cs` asserting `EnsureIconForSession()` on a WPF `TrayIconManager` (with `MinimizeToTray=false, CloseAction=Ask`) creates the `NotifyIcon`, via a thin internal `HasIcon` seam mirroring the Avalonia one (follow the WPF-test STA/`Application` conventions in `tests/TestThreadPoolInitializer.cs` / the WPF test collection). **KNOWN OBSTACLE:** `TrayIconManager.EnsureIcon()` → `LoadAppIcon()` streams a `pack://` WPF resource that THROWS headless (the ctor's try/catch swallows it → `_notifyIcon` stays null → a `HasIcon` seam reads false even after the fix), so this test may be un-runnable in the WPF test infra. This assertion is TRANSIENT (deleted at Task 11) and the fix is already covered by the Avalonia impl test (Step 1), the updated close-handler test (Step 6), the maintainer's smoke (Task 7), and the whole-diff review (Task 12). **If it cannot run green headlessly, DO NOT force it — skip creating the file and note the reason in the commit body.** In that case remove `TrayIconManagerEnsureIconTests.cs` from Task 11's deletion list and Task 2's Files list.

- [ ] **Step 8: Run the changed tests — verify they pass.**

Run: `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests --filter "FullyQualifiedName~EnsureIconForSession|FullyQualifiedName~AskMinimize_NoRemember"` and (only if Step 7 landed) `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests --filter "FullyQualifiedName~TrayIconManagerEnsureIcon"`
Expected: all PASS (the updated `AskMinimize_NoRemember...` now green under the new contract).

- [ ] **Step 9: Both suites green + 0-warning rebuild BOTH heads.**

Run: both `dotnet test` commands (full) + `dotnet build src/CSUploader.Avalonia/... -c Debug -t:Rebuild` + `-c Release -t:Rebuild` + `dotnet build src/CSUploader.csproj -c Debug -t:Rebuild -p:OutDir=D:\temp2\cbuild-mig\wpf` + `-c Release -t:Rebuild`.
Expected: Avalonia **+1** (the new EnsureIconForSession impl test; the close-handler test was UPDATED in place → no count change), WPF+shared **+1 if Step 7 landed, else +0**, both heads 0-warning Debug+Release. Record the new baselines.

- [ ] **Step 10: Commit.**

```bash
# Add tests/Services/TrayIconManagerEnsureIconTests.cs to this list ONLY if Step 7 landed it.
git -C E:\Projects\CSUploader\CSUploader-avalonia add src/CSUploader.Core/Services/ITrayIconService.cs src/Services/TrayIconManager.cs src/CSUploader.Avalonia/Services/AvaloniaTrayIconService.cs src/Views/MainWindow.xaml.cs src/CSUploader.Avalonia/Views/MainWindow.axaml.cs tests/CSUploader.Avalonia.Tests/Services/AvaloniaTrayIconServiceTests.cs tests/CSUploader.Avalonia.Tests/Views/MainWindowCloseToTrayTests.cs
git -C E:\Projects\CSUploader\CSUploader-avalonia commit -m "Phase 9: fix Ask->Minimize-without-Remember tray-strand on both heads via EnsureIconForSession"
```

---

## Task 3: Cross-head ledger fix (b) — `MainViewModel.InitializeAsync` idempotency guard (Core)

`InitializeAsync` is NOT idempotent (loads persisted packages, hydrates+wires log persistence, restores theme, loads settings/proxies/uploaded). The Avalonia App already one-shot-guards the `Opened` re-raise (`App.axaml.cs:142` `hydrated`), but the guard belongs in the VM (defense in depth; a second caller from any head/test path must be safe). Design line 102 item 5 / line 102 item 4. Core-only change; WPF behaviour unchanged (its `Loaded` fires once).

**Files:**
- Modify: `src/CSUploader.Core/ViewModels/MainViewModel.cs` (add `_initialized` guard)
- Test: `tests/ViewModels/MainViewModelInitializeTests.cs` (create — Core/shared, survives cutover)

**Interfaces:**
- Consumes: existing `MainViewModel(IServiceProvider)` + `InitializeAsync()`.
- Produces: `InitializeAsync()` runs its body at most once per instance (second+ calls return immediately).

- [ ] **Step 1: Write the failing test.** Create `tests/ViewModels/MainViewModelInitializeTests.cs`. Build a `MainViewModel` over a DI provider composed with `AddCoreServices` + the test UI-interface mocks/inlines the other MainViewModel tests use (see `tests/ViewModels/MainViewModelUpdateTests.cs` for the exact provider recipe + `InlineUiDispatcher`), call `InitializeAsync()` twice, and assert the once-only effect — the cleanest observable is `PackageManager.LoadPersistedPackagesAsync` running once. Assert via a spy/seam the existing tests use; if none exists, assert that the persisted-package count loaded equals a single load (seed one persisted package into the scratch DAL, call twice, assert the `UploadedViewModel`/package collection did not double):

```csharp
[Fact]
public async Task InitializeAsync_IsIdempotent_RunsBodyOnce()
{
    using MainViewModelHarness h = MainViewModelHarness.CreateWithOnePersistedPackage();
    await h.ViewModel.InitializeAsync();
    int afterFirst = h.PersistedPackagesLoadedCount;
    await h.ViewModel.InitializeAsync();     // second call must be a no-op.
    Assert.Equal(afterFirst, h.PersistedPackagesLoadedCount);
}
```
(Use the neighbouring MainViewModel-test harness; if none is reusable, build the provider inline exactly as `MainViewModelUpdateTests` does and count loads through a `Mock`/fake `PackageManager` seam. Keep the harness disposal — Task 4 makes `MainViewModel` IDisposable.)

- [ ] **Step 2: Run it — verify it fails** (second call re-runs the body; the count doubles).

Run: `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests --filter "FullyQualifiedName~MainViewModelInitialize"`
Expected: FAIL.

- [ ] **Step 3: Add the guard.** In `src/CSUploader.Core/ViewModels/MainViewModel.cs`, add a field and an early return at the top of `InitializeAsync`:

```csharp
    private bool _initialized;
```
```csharp
    public async Task InitializeAsync()
    {
        // Idempotency guard (Phase 9 ledger fix b): InitializeAsync loads persisted packages, wires log
        // persistence, and restores theme — none of it safe to run twice. The Avalonia head can re-raise
        // Window.Opened on every tray restore (App.axaml.cs one-shots the outer call too); guarding here
        // makes the VM safe for any caller/head. WPF's Loaded fires once, so its behaviour is unchanged.
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        FirstRun.InitializeDatabase(_services, _logger);
        // …rest unchanged…
```

- [ ] **Step 4: Run it — verify it passes.**

Run: same `--filter` command.
Expected: PASS.

- [ ] **Step 5: WPF+shared suite green.**

Run: `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests`
Expected: +1 vs the Task 2 baseline, all green.

- [ ] **Step 6: Commit.**

```bash
git -C E:\Projects\CSUploader\CSUploader-avalonia add src/CSUploader.Core/ViewModels/MainViewModel.cs tests/ViewModels/MainViewModelInitializeTests.cs
git -C E:\Projects\CSUploader\CSUploader-avalonia commit -m "Phase 9: make MainViewModel.InitializeAsync idempotent"
```

---

## Task 4: Cross-head ledger fix (c) — `MainViewModel` IDisposable (Core)

`MainViewModel` starts a 6h `_updateTimer` and subscribes an anonymous `Localizer.Instance.PropertyChanged` lambda in its ctor (`MainViewModel.cs:75-79`, `86-87`) plus the named `Logger_OnLogOutput` (`:71`). `Localizer.Instance` is a process-global static, so every ctor leaks a subscription that outlives the VM — the constraint UploadsViewTests' footer-jump harness works around (design line 102 item 5 / item 4). Make it IDisposable; both heads already dispose the provider on exit (`App.axaml.cs:227` / `App.xaml.cs:69`), so the singleton VM is disposed at shutdown, and tests that build a `MainViewModel` must dispose it. Core-only change.

**Files:**
- Modify: `src/CSUploader.Core/ViewModels/MainViewModel.cs` (capture the Localizer handler in a field; implement IDisposable)
- Test: `tests/ViewModels/MainViewModelDisposeTests.cs` (create — Core/shared, survives cutover)

**Interfaces:**
- Produces: `MainViewModel : ObservableObject, IDisposable`. `Dispose()` stops `_updateTimer`, unsubscribes the Localizer handler and `Logger_OnLogOutput`, and is idempotent.

- [ ] **Step 1: Write the failing test.** Create `tests/ViewModels/MainViewModelDisposeTests.cs`: build a `MainViewModel`, capture the `Localizer.Instance` subscriber count before/after Dispose (or, if no count seam, assert that after Dispose a `Localizer` culture flip no longer raises the VM's `PropertyChanged` for `WindowTitle`). Preferred — observe via the VM's own event:

```csharp
[Fact]
public void Dispose_UnsubscribesLocalizer_NoMorePropertyChanged()
{
    CultureInfo saved = Localizer.Instance.Culture;        // process-global static — restore in finally.
    try
    {
        MainViewModel vm = MainViewModelHarness.Create().ViewModel;
        bool raisedAfterDispose = false;

        vm.Dispose();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.WindowTitle)) raisedAfterDispose = true; };
        // Reassigning Localizer.Culture raises PropertyChanged ONLY on a real change (Localizer.cs:43-58 guards
        // Equals(field, value)), so pick a culture != the current one. This would fire the VM's WindowTitle
        // refresh if the ctor subscription were still attached.
        Localizer.Instance.Culture = CultureInfo.GetCultureInfo(saved.Name == "ja" ? "ko" : "ja");

        Assert.False(raisedAfterDispose);
    }
    finally
    {
        Localizer.Instance.Culture = saved;
    }
}
```
(`Localizer.Culture` is a settable property — `src/CSUploader.Core/Lib/Localization/Localizer.cs:43`; add `using System.Globalization;`. Restoring the static culture in `finally` satisfies the `[Fact]` static-state rule.)

- [ ] **Step 2: Run it — verify it fails** (`Dispose` undefined, or the event still fires).

Run: `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests --filter "FullyQualifiedName~MainViewModelDispose"`
Expected: FAIL / build failure.

- [ ] **Step 3: Implement IDisposable.** In `src/CSUploader.Core/ViewModels/MainViewModel.cs`: change the declaration to `public partial class MainViewModel : ObservableObject, IDisposable`; capture the Localizer lambda in a field so it can be detached; add `Dispose`:

```csharp
    private readonly PropertyChangedEventHandler _localizerChanged;
    private bool _disposed;
```
Replace the inline ctor subscription with the captured handler:
```csharp
        // Captured (not inline) so Dispose can detach it — Localizer.Instance is a process-global static,
        // so an un-detached handler leaks the VM for the process lifetime (Phase 9 ledger fix c).
        _localizerChanged = (_, _) =>
        {
            OnPropertyChanged(nameof(ThemeMenuLabel));
            OnPropertyChanged(nameof(WindowTitle));
        };
        Localizer.Instance.PropertyChanged += _localizerChanged;
```
Add at the end of the class:
```csharp
    /// <summary>
    /// Stops the 6h update timer and detaches the process-global Localizer + logger subscriptions. Disposed
    /// with the DI provider at app exit; tests that build a MainViewModel must dispose it (the Localizer
    /// static otherwise accumulates dead subscribers across the run). Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _updateTimer.Stop();
        Localizer.Instance.PropertyChanged -= _localizerChanged;
        _logger.OnLogOutput -= Logger_OnLogOutput;
    }
```
(`IUiTimer` exposes `Stop()` — `src/CSUploader.Core/Services/IUiDispatcher.cs:39`; the ctor calls `_updateTimer.Start()` at `MainViewModel.cs:87`, so `Dispose` calls the sibling `Stop()`. The `InitializeAsync` log-persistence lambda `:201` is intentionally NOT detached here: it is wired only once InitializeAsync runs, captures the repo disposed with the provider, and is out of this fix's design scope — leave a one-line comment saying so.)

- [ ] **Step 4: Run it — verify it passes.**

Run: same `--filter` command.
Expected: PASS.

- [ ] **Step 5: Audit MainViewModel-building tests for disposal.** Grep `tests/` for `new MainViewModel(`/`GetRequiredService<MainViewModel>()`; ensure each test disposes it (`using` or `finally`). The provider-disposing tests already cover the DI-resolved ones; fix any test that builds a bare `MainViewModel` without disposal (the analyzer/warning gate may now flag CA2000-style leaks). Update the UploadsViewTests footer-jump harness comment if it referenced this as a known leak.

- [ ] **Step 6: WPF+shared suite green + 0-warning.**

Run: `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests` + `dotnet build src/CSUploader.Core/CSUploader.Core.csproj -c Debug -t:Rebuild -p:OutDir=D:\temp2\cbuild-mig\core` + `-c Release -t:Rebuild`.
Expected: +1 vs Task 3, all green, 0-warning.

- [ ] **Step 7: Commit.**

```bash
git -C E:\Projects\CSUploader\CSUploader-avalonia add src/CSUploader.Core/ViewModels/MainViewModel.cs tests/ViewModels/MainViewModelDisposeTests.cs
git -C E:\Projects\CSUploader\CSUploader-avalonia commit -m "Phase 9: make MainViewModel IDisposable (stop update timer, detach Localizer/logger)"
```

---

## Task 5: Cross-head ledger fix (d) — Register `UploadWizardViewModel` in DI

Phase 6 hand-builds `UploadWizardViewModel` in both heads' `UploadWizardWindow` code-behind via `App.Services` (Avalonia `UploadWizardWindow.axaml.cs:95-105`; WPF `src/Views/UploadWizardWindow.xaml.cs:27`). Its single public ctor takes 7 args, the last two optional and BOTH DI-registered (`IFileHosterRegistry` at `ServiceRegistration.cs:67`, `IAccountVerifier` at `:174`), so a plain DI registration injects the real graph — identical to the manual build. Register it once in Core (design line 102 item 5 / item 4). Core + both code-behinds; per-open lifetime is **Transient**.

**Files:**
- Modify: `src/CSUploader.Core/ServiceRegistration.cs` (register `UploadWizardViewModel`)
- Modify: `src/CSUploader.Avalonia/Views/UploadWizardWindow.axaml.cs:95-105` (resolve from DI)
- Modify: `src/Views/UploadWizardWindow.xaml.cs` (resolve from DI)
- Test: `tests/CSUploader.Avalonia.Tests/AvaloniaStartupDISmokeTests.cs` (assert resolution)
- Test: `tests/StartupDISmokeTests.cs` (WPF-side assert resolution — transient, but the Core-registration assertion is valid on both; see Task 11 for its deletion/split)

**Interfaces:**
- Produces: `services` resolves `UploadWizardViewModel` (Transient) with the real `PackageManager`, `FileHosterLoginRepository`, `IDialogService`, `IAppLogger`, `AppSettings`, `IFileHosterRegistry`, `IAccountVerifier`.
- Consumes: both `UploadWizardWindow` ctors call `serviceProvider.GetRequiredService<UploadWizardViewModel>()`.

- [ ] **Step 1: Write the failing Avalonia DI-smoke assertion.** In `tests/CSUploader.Avalonia.Tests/AvaloniaStartupDISmokeTests.cs`, add to the existing graph-resolves test (or a sibling `[Fact]`):

```csharp
[Fact]
public void Provider_Resolves_UploadWizardViewModel_Transient()
{
    ServiceCollection services = new();
    App.ConfigureServices(services, TestBaseDir);          // mirror the file's existing composition.
    using ServiceProvider sp = services.BuildServiceProvider();

    UploadWizardViewModel a = sp.GetRequiredService<UploadWizardViewModel>();
    UploadWizardViewModel b = sp.GetRequiredService<UploadWizardViewModel>();
    Assert.NotNull(a);
    Assert.NotSame(a, b);                                   // Transient: a fresh wizard per open.
}
```
(Use the file's actual base-dir/composition helper — match the existing smoke test's setup exactly.)

- [ ] **Step 2: Run it — verify it fails** (`UploadWizardViewModel` not registered → `GetRequiredService` throws).

Run: `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests --filter "FullyQualifiedName~UploadWizardViewModel_Transient"`
Expected: FAIL (`InvalidOperationException: No service for type ... UploadWizardViewModel`).

- [ ] **Step 3: Register it.** In `src/CSUploader.Core/ServiceRegistration.cs`, in the ViewModels block (after `LogsViewModel`, `:189`):

```csharp
        // Transient: a fresh wizard per open (unlike the singleton shell VMs). Both heads' UploadWizardWindow
        // resolve this instead of hand-building it (Phase 9 ledger fix d). The two optional ctor args
        // (IFileHosterRegistry, IAccountVerifier) are registered above, so DI injects the real graph.
        services.AddTransient<UploadWizardViewModel>();
```

- [ ] **Step 4: Switch both code-behinds to DI resolution.**

Avalonia (`src/CSUploader.Avalonia/Views/UploadWizardWindow.axaml.cs`), replace `BuildViewModel()`'s body:
```csharp
    private static UploadWizardViewModel BuildViewModel()
        => ((App)Application.Current!).Services.GetRequiredService<UploadWizardViewModel>();
```
WPF (`src/Views/UploadWizardWindow.xaml.cs:26-34`), replace the `IServiceProvider sp = ((App)Application.Current).Services;` line AND the `_vm = new UploadWizardViewModel(...)` seven-arg construction with a single resolve (the window has no `_serviceProvider` field — it reaches DI via `((App)Application.Current).Services`, line 26):
```csharp
        _vm = ((App)Application.Current).Services.GetRequiredService<UploadWizardViewModel>();
```
Remove the now-unused `sp` local; `using Microsoft.Extensions.DependencyInjection;` is already present.

- [ ] **Step 5: Add the WPF-side smoke assertion.** In `tests/StartupDISmokeTests.cs`, add the same `GetRequiredService<UploadWizardViewModel>()` resolution assertion (this test is deleted/split at Task 11, but validates the Core registration on the WPF composition meanwhile).

- [ ] **Step 6: Run both smoke tests — verify they pass.**

Run: both `dotnet test ... --filter "FullyQualifiedName~UploadWizardViewModel"` commands.
Expected: PASS.

- [ ] **Step 7: Both suites green + 0-warning rebuild BOTH heads.**

Run: full `dotnet test` both projects + `-t:Rebuild` Debug+Release both heads.
Expected: Avalonia +1, WPF+shared +1, 0-warning. Record baselines.

- [ ] **Step 8: Commit.**

```bash
git -C E:\Projects\CSUploader\CSUploader-avalonia add src/CSUploader.Core/ServiceRegistration.cs src/CSUploader.Avalonia/Views/UploadWizardWindow.axaml.cs src/Views/UploadWizardWindow.xaml.cs tests/CSUploader.Avalonia.Tests/AvaloniaStartupDISmokeTests.cs tests/StartupDISmokeTests.cs
git -C E:\Projects\CSUploader\CSUploader-avalonia commit -m "Phase 9: register UploadWizardViewModel in DI; both heads resolve it instead of hand-building"
```

---

## Task 6: Parity sweep — per-grid + per-view interaction checklist (agent + the maintainer split)

Drive the LIVE seeded Avalonia head (`--agent`, scratch DB) through the interaction checklist per grid and per view, confirming Phase 5-8 behaviours survived intact after the header-metrics pass and ledger fixes. This is a VERIFICATION checkpoint: it writes no product code UNLESS it finds a regression, in which case STOP and fix it with a failing-test-first cycle (systematic-debugging + TDD) before resuming the sweep. Record the result table into the plan/design reconcile (Task 12 folds it into the Phase 9 RECONCILED block).

**Files:**
- No product files unless a regression is found (then: the offending head file + a headless regression test).
- Record: the sweep result table (pasted into the Task 12 design-reconcile commit).

**Preconditions:** `dotnet build src/CSUploader.Avalonia/... -c Debug -p:OutDir=D:\temp2\cbuild-mig\ava` → `dotnet run scripts/seed-fake-data.cs -- D:\temp2\cbuild-mig\ava` → launch `CSUploader.Avalonia.exe --agent` via `scripts/ava-drive.cs`. Single-driver lock: no concurrent MCP session.

- [ ] **Step 1: AGENT-VERIFIABLE per-grid checklist.** For EACH grid — UploadsView, UploadedView (grouped), SettingsView accounts, SettingsView proxies, LogsView — drive via the bridge and record pass/fail:
  - **Right-click targeting:** right-click an UNSELECTED row selects it before the context menu opens (the bridge rightclick raises `ContextRequested`; `SelectRowOnRightClick` — design line 101 reconcile item 7); right-click inside a multi-selection preserves it.
  - **Empty-click clear:** clicking the grid's empty area clears selection (needs the themed Background — Phase 5 port rule).
  - **Delete key:** Delete removes the selected row where the grid has a Delete `KeyBinding` (UploadsView, proxies, wizard files).
  - **Delete-inside-editor edits text (the guard):** with a text cell-editor focused (proxies Host/Port/User, the Order cell), Delete edits the TEXT, does NOT delete the row — `DataGridDeleteKeyGuard.EditorGuardedCommand` (design line 101 reconcile item 10).
  - **Enter / double-click open:** LogsView Enter (tunnel KeyDown) and double-click (DoubleTapped + row hit-test) open the log-details window.
  - **Auto-scroll:** appending rows auto-scrolls to the newest (AutoScrollBehavior) where the WPF grid did.
  - **Ctrl+C AND Ctrl+Insert copy:** both gestures produce the package-EXPANDING copy on UploadsView (design line 101 reconcile item 10; the built-in DataGrid maps Insert→ProcessCopyKey). Read the result via the clipboard service seam or a headless copy test if the bridge cannot read the OS clipboard.
  - **Settings persistence:** toggle a column's visibility/order and the theme; relaunch the head against the SAME scratch DB; confirm the choice restored (column visibility+order only — WPF never persisted WIDTH, design line 72).
- [ ] **Step 2: AGENT-VERIFIABLE per-view checklist.** Menus (File/View/Help structure + `IsChecked` two-way theme toggle — port rule 46), context menus (UploadsView whitespace-suppressed menu; the plain ContextMenus without a cancelling `Opening` handler are bridge-openable — design line 101 reconcile item 7), and key bindings open/read correctly. Confirm the theme toggle on the menu bar flips light/dark live.
- [ ] **Step 3: NAME the DO-NOT-CLICK surfaces and DO NOT invoke them.** In EditAccount, the sweep may inspect but MUST NOT click **"Sign in…"**, and in Settings/accounts MUST NOT click **"Check account"** or **"Refresh all accounts"** — all three reach real hosters (design line 103 item 10). Record them as observed-present, not exercised.
- [ ] **Step 4: On any regression — STOP and fix TDD-first.** Reproduce headlessly, write the failing `[AvaloniaFact]`, fix the offending head file, green the test, rebuild 0-warning, commit as its own `Phase 9: fix <regression>` commit. Then resume the sweep.
- [ ] **Step 5: Record the maintainer-ONLY items as deferred to Task 7** (not failures): native WebView typing/focus, Turnstile, 125/150% DPI, Win10 DWM visual.
- [ ] **Step 6: Write the sweep result table** (grid × checklist item = pass/defer-to-the maintainer/fixed-commit) into a scratch note for Task 12's reconcile block. No commit here unless a regression was fixed.

---

## Task 7: the maintainer's manual smoke — HARD HUMAN GATE (blocks all deletion tasks)

A human gate the agent CANNOT perform. The WPF head MUST still exist at this checkpoint (it is the byte-comparable reference). Hand this to the team lead → the maintainer; do NOT proceed to Task 8 until the maintainer returns GO. This task writes no code.

**Deliverable:** the maintainer exercises the Debug/Release Avalonia head (a REAL build, NOT `--agent`, against a real or test-account DB) and confirms:
- [ ] **E2E upload:** one real end-to-end upload of a real file through the wizard to a live hoster (the maintainer's choice), completing with a link + the completion toast.
- [ ] **Real WebView sign-in incl. Turnstile:** one captcha-gated hoster sign-in (e.g. ex-load/HitFile/FileBoom), typing into the WebView and solving the Turnstile challenge; the cookie/token captures and the account verifies.
- [ ] **Native focus:** Tab-out of the WebView page returns focus to the host chrome (`MoveFocusRequested`); re-activating the window returns focus into the page (focus-on-activation).
- [ ] **DPI:** at 125% AND 150% display scaling — the WebView bounds track the host on resize/move, and completion toasts place bottom-right in the correct work area.
- [ ] **Win10 DWM dark title bar:** on a Windows 10 box (the dev box is Win11 — design line 102 reconcile item 7), dark mode applies the attr-19 immersive dark title bar on the main + child windows.
- [ ] the maintainer records GO / NO-GO. On NO-GO: file the defect, fix it (a new TDD task on the branch, WPF head still present as reference), re-smoke. On GO: proceed to Task 8. **Deletion (Tasks 8+) is BLOCKED until GO.**

---

## Task 8: Tag `last-wpf` — BEFORE deletion (emergency re-release anchor)

Design line 108. Tag the last commit where the WPF head still exists so an emergency WPF re-release is one `git checkout` away.

- [ ] **Step 1: Confirm HEAD is post-GO and both heads build/test green** (the tip of Task 6/7's work).
- [ ] **Step 2: Tag.**

```bash
git -C E:\Projects\CSUploader\CSUploader-avalonia tag last-wpf
git -C E:\Projects\CSUploader\CSUploader-avalonia tag --list last-wpf
```
Expected: `last-wpf` listed, pointing at the current tip. (Push happens with the branch at integration time; the tag is local until then.)

---

## Task 9: Cutover — delete the WPF head

Remove the WPF head and every WPF-head-only source file; KEEP the two nested projects and the SHARED image tree the Avalonia head links. Precise inventory (verified against `src/` layout + the Avalonia csproj's `..\Properties\Images` link at `CSUploader.Avalonia.csproj:67,76` and `..\..\external\vscode-icons` at `:83`).

**Files — DELETE (WPF head):**
- `src/CSUploader.csproj`
- `src/App.xaml`, `src/App.xaml.cs`
- `src/GlobalUsings.cs` (WPF-head global usings; Core + Avalonia have their own)
- `src/Behaviors/` (WPF behaviors)
- `src/Converters/` (WPF converters — Avalonia twins live in `src/CSUploader.Avalonia/Converters/`)
- `src/Lib/` (WPF head Lib/UI — e.g. `ImmersiveDarkMode`; Core's Lib is `src/CSUploader.Core/Lib/`)
- `src/Resources/` (WPF XAML resource dictionaries — e.g. `ImageResources.xaml`, `Tokens.xaml`; the resx live in Core)
- `src/Services/` (all WPF impls: `DialogService`, `TrayIconManager`, `WpfClipboardService`, `WpfThemeApplier`, `WpfUiDispatcher`, `WpfUpdateProgressSink`, `WpfFontEnumerationService`, `DefaultToastWindowFactory`, `ToastWindowHost`, `ReferenceShotCapture`, `WebViewInteractiveAuthService`)
- `src/Views/` (all WPF `.xaml` + `.xaml.cs`)
- `src/Properties/Resources.Designer.cs`, `src/Properties/Resources.resx`, `src/Properties/Settings.settings`

**Files — KEEP (do NOT delete):**
- `src/CSUploader.Core/` (the shared core)
- `src/CSUploader.Avalonia/` (the surviving head)
- `src/Properties/Images/` (linked by the Avalonia head — `<AvaloniaResource Include="..\Properties\Images\**">` + `ApplicationIcon`)
- `external/` (vscode-icons submodule — repo-root, linked by both)
- `src/stylecop.json` — verify first (Step 1); if no surviving csproj references it, it is vestigial and MAY be deleted, but the 0-warning Rebuild gate (Step 3) is the safety net either way.

**Files — MODIFY (must be edited, NOT deleted):**
- `CSUploader.sln` — line 6 lists the WPF head `src\CSUploader.csproj` (GUID `{79AFEA99-…}`). **`release.yml:43-44` runs SOLUTION-scoped `dotnet restore` + `dotnet test`**, so if the WPF project file is `git rm`'d but the sln still references it, the release job dies at "Restore + test" ("project does not exist") — and every plan gate is per-PROJECT (`-p:OutDir`), so this stays INVISIBLE until CI. It MUST be removed from the sln in the same commit (Step 2). (The `last-wpf` tag keeps the intact sln for emergency rollback — unaffected.)

- [ ] **Step 1: Verify no surviving project references a to-be-deleted path, and confirm the sln still lists the WPF head.** Grep the KEPT csprojs (`CSUploader.Core.csproj`, `CSUploader.Avalonia.csproj`) + `.mcp.json` + `Directory.Build.local.props` for any of `src/Services`, `src/Views`, `src/Resources`, `src/Lib`, `src/Behaviors`, `src/Converters`, `stylecop.json`. Confirm the Avalonia csproj's `..\Properties\Images\**` and `..\Properties\Images\Logo\icon.ico` resolve to `src/Properties/Images` (which survives). Confirm `src/stylecop.json` is unreferenced (grep found none at plan time). Confirm `CSUploader.sln:6` still references `src\CSUploader.csproj` (it does at plan time) so Step 2's `dotnet sln remove` has a target.

Run: `git -C E:\Projects\CSUploader\CSUploader-avalonia grep -nE "src/Services|src/Views|src/Resources|src/Lib/|src/Behaviors|src/Converters|stylecop.json" -- "src/CSUploader.Avalonia/*.csproj" "src/CSUploader.Core/*.csproj" .mcp.json` and `git -C E:\Projects\CSUploader\CSUploader-avalonia grep -n "CSUploader.csproj" -- CSUploader.sln`
Expected: no csproj/.mcp hits into deleted paths (the Images link is via `..\Properties\Images`, not the deleted dirs); the sln grep DOES hit line 6 (that reference is removed in Step 2).

- [ ] **Step 2: Remove the WPF project from the solution, THEN delete the head.** Run `dotnet sln remove` FIRST (while the csproj still exists so the path matches — it strips both the `Project(...)` entry and the WPF head's `GlobalSection` config-platform lines), then `git rm` the files, then stage the modified sln:

```bash
git -C E:\Projects\CSUploader\CSUploader-avalonia sln CSUploader.sln remove src/CSUploader.csproj
git -C E:\Projects\CSUploader\CSUploader-avalonia rm -r \
  src/CSUploader.csproj src/App.xaml src/App.xaml.cs src/GlobalUsings.cs \
  src/Behaviors src/Converters src/Lib src/Resources src/Services src/Views \
  src/Properties/Resources.Designer.cs src/Properties/Resources.resx src/Properties/Settings.settings
git -C E:\Projects\CSUploader\CSUploader-avalonia add CSUploader.sln
```
(Delete `src/stylecop.json` too only if Step 1 confirmed it unreferenced. Do NOT `git rm` `src/Properties/Images`, `src/CSUploader.Core`, or `src/CSUploader.Avalonia`. Verify `CSUploader.sln` now lists only `CSUploader.Core`, `CSUploader.Avalonia`, `CSUploader.Tests`, `CSUploader.Avalonia.Tests` — the WPF `{79AFEA99-…}` GUID must be gone from every section.)

- [ ] **Step 3: Rebuild the Avalonia head + Core, 0-warning Debug+Release, confirm nothing shared was lost.**

Run: `dotnet build src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Debug -t:Rebuild -p:OutDir=D:\temp2\cbuild-mig\ava` and `-c Release -t:Rebuild`.
Expected: builds 0-warning; the linked images + file-type SVGs still resolve (the submodule + Images survived). If a build error names a deleted path, a KEEP item was wrong — restore it. NOTE: `tests/CSUploader.Tests.csproj` will now FAIL to restore/build (it still references the deleted `src/CSUploader.csproj`) — that is EXPECTED and fixed in Task 11; do not run that suite here. A SOLUTION-scoped `dotnet restore CSUploader.sln` (what `release.yml` runs) therefore ALSO stays broken until Task 11 retargets the test project — the CI-parity solution-restore gate lives at Task 11 Step 5, not here.

- [ ] **Step 4: Commit (deletion only — retarget/rename land in Tasks 10-11).**

```bash
git -C E:\Projects\CSUploader\CSUploader-avalonia commit -m "Phase 9 cutover: delete the WPF head (Avalonia head is now the app)"
```

---

## Task 10: Cutover — Avalonia head takes `AssemblyName`/`packId` CSUploader; manifest; release.yml

Rebrand the surviving head to the Velopack identity (`CSUploader`) and re-point packaging. avares URIs are rename-proof (root-relative in XAML; assembly-name-derived in code — `AvaloniaTrayIconService.cs:25-26` reads `typeof(App).Assembly.GetName().Name`), so only build/manifest identity changes.

**Files:**
- Modify: `src/CSUploader.Avalonia/CSUploader.Avalonia.csproj:22` (`AssemblyName`)
- Modify: `src/CSUploader.Avalonia/app.manifest:3` (`assemblyIdentity` name)
- Modify: `.github/workflows/release.yml:48` (publish path)

**Interfaces:**
- Produces: `CSUploader.exe` from the Avalonia head; `vpk pack --packId CSUploader --mainExe CSUploader.exe` unchanged (design line 20).

- [ ] **Step 1: Set the AssemblyName.** In `src/CSUploader.Avalonia/CSUploader.Avalonia.csproj`, change `<AssemblyName>CSUploader.Avalonia</AssemblyName>` → `<AssemblyName>CSUploader</AssemblyName>`. (Keep the csproj FILENAME `CSUploader.Avalonia.csproj` — DECISION default; renaming is cosmetic and churns every ProjectReference. `RootNamespace=CSUploader` already correct.)

- [ ] **Step 2: Update the manifest identity.** In `src/CSUploader.Avalonia/app.manifest`, change `<assemblyIdentity version="1.0.0.0" name="CSUploader.Avalonia"/>` → `name="CSUploader"`.

- [ ] **Step 3: Re-point release.yml.** In `.github/workflows/release.yml`, change the publish line (`:48`) `dotnet publish src/CSUploader.csproj` → `dotnet publish src/CSUploader.Avalonia/CSUploader.Avalonia.csproj`. Leave `--packId CSUploader`, `--mainExe CSUploader.exe`, and the version-derive/notes steps unchanged (the built exe is now `CSUploader.exe` because `AssemblyName=CSUploader`).

- [ ] **Step 4: Grep-audit for hardcoded old identity.** Confirm nothing hardcodes the assembly name `"CSUploader.Avalonia"` for an avares authority or an assembly lookup (the tray derives it; XAML uses root-relative URIs):

Run: `git -C E:\Projects\CSUploader\CSUploader-avalonia grep -n "CSUploader\.Avalonia" -- "src/CSUploader.Avalonia/**/*.cs" "src/CSUploader.Avalonia/**/*.axaml"`
Expected: only benign hits (namespaces are `CSUploader`, not `CSUploader.Avalonia`; the InternalsVisibleTo target `CSUploader.Avalonia.Tests` is the TEST assembly name and is CORRECT — leave it). Any hardcoded `avares://CSUploader.Avalonia/...` authority must become the assembly-name-derived form or root-relative.

- [ ] **Step 5: Publish locally + confirm `CSUploader.exe` emerges and launches.**

Run (PowerShell): `dotnet publish src/CSUploader.Avalonia/CSUploader.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o D:\temp2\cbuild-mig\publish` then confirm `D:\temp2\cbuild-mig\publish\CSUploader.exe` exists; launch it once with `--agent` against a scratch DB (agent-safe — the shell opens, no real upload) and confirm the window + tray + tabs render, then close.
Expected: `CSUploader.exe` present; the shell launches; tray icon resolves (proves the assembly-name-derived avares authority works under the new name).

- [ ] **Step 6: Commit.**

```bash
git -C E:\Projects\CSUploader\CSUploader-avalonia add src/CSUploader.Avalonia/CSUploader.Avalonia.csproj src/CSUploader.Avalonia/app.manifest .github/workflows/release.yml
git -C E:\Projects\CSUploader\CSUploader-avalonia commit -m "Phase 9 cutover: Avalonia head takes AssemblyName/manifest CSUploader; re-point release.yml"
```

---

## Task 11: Cutover — retarget the test project; delete WPF-coupled tests; record baseline; i18n green

`tests/CSUploader.Tests.csproj` still references the deleted WPF head. Re-target it to `CSUploader.Core`, delete the WPF-head-only tests (their Avalonia twins already exist), fix the one WPF-service usage, and record the emergent green count as the new permanent baseline. Design line 37 ("Retargets to Core … at cutover") + item 11.

**Files:**
- Modify: `tests/CSUploader.Tests.csproj:33` (ProjectReference → Core)
- Delete: `tests/Converters/ConverterTests.cs` (WPF `System.Windows.Media` + WPF converters; Avalonia twin `tests/CSUploader.Avalonia.Tests/Converters/ConverterTests.cs` exists)
- Delete: `tests/StartupDISmokeTests.cs` (WPF head composition via `WpfUiDispatcher`; Avalonia twin `AvaloniaStartupDISmokeTests.cs` exists)
- Delete (ONLY if Task 2 Step 7 landed it): `tests/Services/TrayIconManagerEnsureIconTests.cs` (Task 2's WPF-side transient test — `TrayIconManager` is gone; skip this line if Step 7 dropped the test as non-headless-runnable)
- Modify: `tests/ViewModels/MainViewModelUpdateTests.cs:63` (`WpfClipboardService` → a mock)
- Modify (stale-wording refresh, folded in from the T5 gate — the wizard VM is now DI-registered Transient, so "hand-construction / NOT DI-registered" test wording is false): `tests/CSUploader.Avalonia.Tests/Views/UploadWizardShellTests.cs` (the `HandConstruction_SevenWizardServices_AllResolveFromTheHeadDiGraph` method name @:46, the `// ── The hand-construction resolves ──` section header @:43, the summary "seven-service hand-construction" @:32, and the body comment `// It is NOT DI-registered, so this asserts…` @:48-49) + `tests/CSUploader.Avalonia.Tests/Views/UploadsViewTests.cs:880` ("the production ctor path is covered by the Task 7 DI hand-construction test"). NOTE these are Avalonia.Tests files (this task's retarget doesn't touch them) — they're folded here purely because this is the test-cleanup task. The test's ASSERTION stays valid (the 7 ctor deps still must resolve); only the wording is stale. Renaming the public method is fine (no external caller); keep the section-header edit ASCII (the a11y hook rejects raw non-ASCII — the `──`/`§` glyphs must be rewritten in ASCII if the line is touched).
- Compiler-guided: any other test that instantiates a deleted WPF-head type.

**Interfaces:**
- Produces: `tests/CSUploader.Tests.csproj` references `..\src\CSUploader.Core\CSUploader.Core.csproj`; the suite holds only Core/shared framework-free tests.

- [ ] **Step 1: Retarget the project reference.** In `tests/CSUploader.Tests.csproj`, change `<ProjectReference Include="..\src\CSUploader.csproj" />` → `<ProjectReference Include="..\src\CSUploader.Core\CSUploader.Core.csproj" />`. (Core already declares `InternalsVisibleTo("CSUploader.Tests")`, so internal Core members — e.g. `ConnectionManagerViewModel.TryParseProxyLine` — stay visible. The design's "+ Avalonia head" is unnecessary: Avalonia-specific tests already live in `CSUploader.Avalonia.Tests`; add an Avalonia head reference ONLY if a surviving test needs an Avalonia type — none should after Step 3.)

- [ ] **Step 2: Delete the WPF-head-only tests.**

```bash
# Append tests/Services/TrayIconManagerEnsureIconTests.cs ONLY if Task 2 Step 7 landed it.
git -C E:\Projects\CSUploader\CSUploader-avalonia rm tests/Converters/ConverterTests.cs tests/StartupDISmokeTests.cs
```

- [ ] **Step 3: Fix the WPF-service usage in the surviving VM test.** In `tests/ViewModels/MainViewModelUpdateTests.cs`, replace `sc.AddSingleton<IClipboardService, WpfClipboardService>();` (`:63`) with `sc.AddSingleton(Mock.Of<IClipboardService>());` (the test asserts VM/update behaviour, not a real clipboard). Remove the now-unused `using` for the WPF service if present.

- [ ] **Step 3b: Refresh the two stale "hand-construction / NOT DI-registered" wordings** (folded in from the T5 gate; see the Files list). In `tests/CSUploader.Avalonia.Tests/Views/UploadWizardShellTests.cs`, rename the method + section header + summary + body comment to reflect that `UploadWizardViewModel` is now DI-registered (Transient) and resolved from the container — the test still validly asserts the 7 ctor dependencies resolve from the head DI graph (keep that assertion; only the wording changes; ASCII-only in any touched line). In `tests/CSUploader.Avalonia.Tests/Views/UploadsViewTests.cs:880`, fix the "Task 7 DI hand-construction test" phrase. Rebuild the Avalonia test project 0-warning; the Avalonia suite count is unchanged (rename ≠ new test).

- [ ] **Step 4: Build the test project — let the compiler enumerate any remaining WPF coupling.**

Run: `dotnet build tests/CSUploader.Tests.csproj -c Debug -p:OutDir=D:\temp2\cbuild-mig\tests`
Expected: either it builds, or errors name the exact remaining WPF-head references. For each: if the test has an Avalonia twin, `git rm` it; if it merely instantiated a WPF service, swap to a mock (as Step 3). Iterate until it builds clean.

- [ ] **Step 5: CI-parity solution restore + both suites green; RECORD the emergent baseline.** FIRST run the exact solution-scoped commands `release.yml:43-44` runs, to prove the Task-9 sln edit + this retarget together unblock the release job (this is the check that was impossible at Task 9 while the test project still dangled):

Run: `dotnet restore CSUploader.sln` then `dotnet build CSUploader.sln -c Release` (solution-scoped, mirrors CI). Then the per-project suites: `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests` and `dotnet test tests/CSUploader.Avalonia.Tests/CSUploader.Avalonia.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\ava-tests`.
Expected: the solution restores + builds with NO "project does not exist" / dangling-reference error (proves the release job will restore); BOTH suites green. The Core/shared suite's new count = (Task-5 WPF+shared baseline) − (ConverterTests + StartupDISmokeTests + TrayIconManagerEnsureIconTests-if-it-landed + any Step-4 deletions). **Write the exact number here as the new permanent baseline** and note it supersedes the "1201" gate for all later work. Avalonia suite unchanged (~451 after Tasks 1/2/5).

- [ ] **Step 6: i18n `--check` green (permanent gate).**

Run: `dotnet test tests/CSUploader.Tests.csproj -p:OutDir=D:\temp2\cbuild-mig\tests --filter "FullyQualifiedName~I18nRegenGate"` AND, for each language, `python scripts/md-to-resx.py docs/i18n-inventory<.lang>.md src/CSUploader.Core/Resources/Strings<.lang>.resx --check`.
Expected: all `OK: … matches regen …`. (This phase changed no resx; the gate should be untouched-green.)

- [ ] **Step 7: Commit.**

```bash
git -C E:\Projects\CSUploader\CSUploader-avalonia add tests/CSUploader.Tests.csproj tests/ViewModels/MainViewModelUpdateTests.cs
git -C E:\Projects\CSUploader\CSUploader-avalonia commit -m "Phase 9 cutover: retarget CSUploader.Tests to Core; drop WPF-coupled tests (new baseline: <N>)"
```

---

## Task 12: Cutover — README/docs; design reconcile; final whole-diff review

**Files:**
- Modify: `README.md:27` (run command → Avalonia head)
- Modify: `CLAUDE.md` (only if it names WPF specifics — audit)
- Modify: `docs/superpowers/specs/2026-07-10-avalonia-migration-design.md` (append the Phase 9 RECONCILED block to the Phase 9 bullet)

- [ ] **Step 1: Fix the README run command.** In `README.md`, change `dotnet run --project src/CSUploader.csproj` → `dotnet run --project src/CSUploader.Avalonia/CSUploader.Avalonia.csproj`. Scan the rest of `README.md` for other WPF/`src/CSUploader.csproj`/`.xaml` mentions and update any that describe the current app (leave historical/changelog references intact).

- [ ] **Step 2: Audit CLAUDE.md + docs.** Grep `CLAUDE.md` and top-level docs for "WPF"/"src/CSUploader.csproj" that describe the LIVE app (not history); update as needed. The dotnet-skills routing guidance is framework-agnostic — likely no change.

- [ ] **Step 3: Append the Phase 9 RECONCILED block.** In the design doc's Phase 9 bullet, add a `**RECONCILED (phase gate 2026-07-…, tag `phase9-cutover-ready`, commits …):**` block mirroring Phases 6-8: the header-metrics values shipped, the four ledger fixes, the Task 6 sweep result table, the maintainer's Task 7 GO, the deletion inventory, the new test baseline, the AssemblyName/manifest/release.yml cutover, and any accepted divergences / DECISIONS resolved. Fold in the Task 6 sweep table.

- [ ] **Step 4: Final whole-diff review.** Invoke `superpowers:requesting-code-review` over `git -C E:\Projects\CSUploader\CSUploader-avalonia diff last-wpf..HEAD` (the whole cutover) PLUS the ledger-fix commits before `last-wpf`. Scrutinise especially the Core + WPF-head touches (Tasks 2-5) and the deletion inventory (nothing shared lost). Address findings (receiving-code-review) with fix-then-test cycles; re-run both suites + 0-warning Release rebuild of the Avalonia head after any change.

- [ ] **Step 5: i18n `--check` + both suites green + 0-warning Release rebuild (final gate).**

Run: both `dotnet test` + all six `md-to-resx.py … --check` + `dotnet build src/CSUploader.Avalonia/... -c Release -t:Rebuild`.
Expected: all green, 0 warnings.

- [ ] **Step 6: Commit + tag the cutover-ready gate.**

```bash
git -C E:\Projects\CSUploader\CSUploader-avalonia add README.md CLAUDE.md docs/superpowers/specs/2026-07-10-avalonia-migration-design.md
git -C E:\Projects\CSUploader\CSUploader-avalonia commit -m "Phase 9 cutover: README/docs + design reconcile; final whole-diff review"
git -C E:\Projects\CSUploader\CSUploader-avalonia tag phase9-cutover-ready
```

---

## Task 13: Release staging as a GitHub PRERELEASE (PREPARE + the maintainer handoff)

Stage the first Avalonia release so INSTALLED apps don't see it until update-in-place is verified. `UpdateService` uses `GithubSource(prerelease: false)` — a GitHub release flagged prerelease is invisible to installs. The plan PREPARES (version bump, notes, workflow prerelease flag); the maintainer TRIGGERS (push tag, verify, promote) — the session's local gh 404s the private repo; never `gh release create` locally (memory: [[release-process]]). Follows the release-process convention: bump csproj + `docs/release-notes/<tag>.md`, push `vX.Y.Z` → `release.yml` builds Velopack + creates the Release.

**Files:**
- Modify: `src/CSUploader.Avalonia/CSUploader.Avalonia.csproj` (add `<Version>`/`<AssemblyVersion>`/`<FileVersion>`)
- Create: `docs/release-notes/v0.0.7.md` (hand-written changelog — the first Avalonia release; confirm the version number with the team lead)
- Modify: `.github/workflows/release.yml` (add `--prerelease` to BOTH `gh release create` branches — TEMPORARY, reverted after promotion)

- [ ] **Step 1: Bump the version in the Avalonia csproj.** Add (the WPF head carried `0.0.6`; the first Avalonia release is the next patch — CONFIRM the number with the team lead):

```xml
    <Version>0.0.7</Version>
    <AssemblyVersion>0.0.7.0</AssemblyVersion>
    <FileVersion>0.0.7.0</FileVersion>
```
(release.yml derives the version from the tag and passes `-p:Version`, so the csproj value is the loose-build fallback — keep it in sync with the tag.)

- [ ] **Step 2: Write the release notes.** Create `docs/release-notes/v0.0.7.md` — a hand-written changelog headlining the Avalonia UI cutover (feature/visual parity, same updater/packId, same hosters). No auto-appended section (memory [[release-process]]).

- [ ] **Step 3: Add the TEMPORARY prerelease flag.** In `.github/workflows/release.yml`, append `--prerelease` to BOTH `gh release create` invocations (the notes-file branch and the `--generate-notes` branch), with a comment: `# TEMP (Phase 9 staged cutover): first Avalonia release ships as a prerelease so installs don't see it until update-in-place is verified; REMOVE after promotion.` (Race-free vs marking prerelease in the UI after a full release is created — an install could poll in the gap.)

- [ ] **Step 4: Commit the preparation.**

```bash
git -C E:\Projects\CSUploader\CSUploader-avalonia add src/CSUploader.Avalonia/CSUploader.Avalonia.csproj docs/release-notes/v0.0.7.md .github/workflows/release.yml
git -C E:\Projects\CSUploader\CSUploader-avalonia commit -m "Phase 9: prepare v0.0.7 (first Avalonia release, staged as prerelease)"
```

- [ ] **Step 5: HANDOFF to the team lead → the maintainer.** The plan stops here; the maintainer (whose gh has repo access) drives:
  1. **Reconcile Buzzheavier FIRST** (DECISION 4): decide whether the in-flight main-tree Buzzheavier work lands before this integration (author it against Core paths while both heads exist — recommended) or is deferred; re-applying its old-layout WPF-head edits AFTER the head is deleted is materially harder (rename detection maps nothing; the edited files are gone) and needs the manual Core re-home + Avalonia avares PNG entry spelled out in DECISION 4. Then integrate the branch to master (finishing-a-development-branch — this is the single final integration since Phase 1 never merged back) and push the `v0.0.7` tag. `release.yml` builds the Velopack package and creates the GitHub release **flagged prerelease** (Step 3), with the `last-wpf` tag pushed for emergency re-release.
  2. On a machine with an EXISTING older install, confirm the installed app does NOT auto-update to v0.0.7 (prerelease invisible — `prerelease:false`).
  3. Manually install the v0.0.7 prerelease assets over that existing install; confirm update-in-place succeeds and the Avalonia app launches with settings/DB intact.
  4. On GO: promote — edit the GitHub release to UNCHECK prerelease; confirm installs now see + apply v0.0.7 on their next poll.
  5. Post-promotion: revert the TEMPORARY `--prerelease` flag (Step 3) so subsequent releases ship as full releases by default (a follow-up commit on master).

---

## Self-review

**1. Spec coverage (design lines 104-109 + ledger):** Pre-sweep header metrics → Task 1. Parity sweep (menus/context/key-bindings/per-grid checklist incl. Delete-in-editor + Ctrl+C/Ctrl+Insert) → Task 6. Cross-head ledger (a) tray-strand → Task 2, (b) InitializeAsync idempotency → Task 3, (c) MainViewModel IDisposable → Task 4, (d) wizard DI → Task 5. the maintainer manual smoke → Task 7. `last-wpf` tag → Task 8. Delete WPF head + **`dotnet sln remove` (release-build blocker)** → Task 9. AssemblyName/packId/manifest/release.yml → Task 10. Test retarget + **CI-parity solution restore** + i18n → Task 11. README/docs + reconcile + whole-diff review → Task 12. Prerelease staging + update-in-place + promote + **Buzzheavier reconciliation** → Task 13. Design-line-107 app.manifest name → Task 10 Step 2. All covered. Six PENDING decisions surfaced (message-box icons, ProgressWindow, PasswordChar, Phase-1-merge status + Buzzheavier re-home, info-toast flip, `--agent` latch) + two minors.

**2. Placeholder scan:** No "TBD"/"handle edge cases"/"similar to Task N". The two genuinely execution-emergent numbers are called out with the exact formula to compute them: the post-deletion test baseline (Task 11 Step 5 — recorded at execution) and the header-metrics tuned values (Task 1 — concrete starting values 16 / `4,0` with a HARD contact-sheet gate). The resource KEY name is gated behind an explicit Step-0 confirmation + a HARD visual gate (Task 1 Step 6). The one genuinely-conditional artifact (the WPF-side `TrayIconManagerEnsureIconTests`, which may not run headlessly — its `pack://` icon load throws) is explicitly conditional in Task 2 Step 7 and cross-referenced in Task 11, not a placeholder.

**3. Type consistency:** `EnsureIconForSession()` is named identically in the interface, both impls, both call sites, the Avalonia impl test, and the UPDATED existing close-handler test. `_initialized` (Task 3) and `_disposed`/`_localizerChanged`/`Dispose()` (Task 4) are consistent. `AddTransient<UploadWizardViewModel>()` (Task 5) matches the confirmed single 7-arg ctor with two DI-registered optionals; the WPF resolve uses the confirmed `((App)Application.Current).Services` accessor. `HasIcon` seam named consistently across the Avalonia impl + its test. The retarget path `..\src\CSUploader.Core\CSUploader.Core.csproj` (Task 11) matches the layout. **VERIFIED against source (not merely flagged):** `internal readonly record struct CloseActionChoice(CloseAction Action, bool Remember)` (`CloseActionDialog.axaml.cs:47`); the MainWindow production ctor `(AppSettings, ITrayIconService, SettingRepository)`; the existing test `AskMinimize_NoRemember_HidesWithoutBalloon_AndDoesNotPersist` at `MainWindowCloseToTrayTests.cs:150-180` with the `UpdateVisibility Times.Once` assert at `:169`; `Localizer.Culture` settable at `Localizer.cs:43`; `IUiTimer.Stop()` at `IUiDispatcher.cs:39`; `CSUploader.sln:6` → `src\CSUploader.csproj`.

**4. Review fixes applied (2026-07-15 adversarial plan review):** MUST-FIX 1 (sln never updated → CI release restore fails) → Task 9 Files/Step 1/Step 2 `dotnet sln remove` + Task 11 Step 5 CI-parity restore. MUST-FIX 2 (Task 2 broke the existing `:169` test) → Task 2 Step 6 now UPDATES that test in place (no duplicate). HANDOFF (Buzzheavier) → DECISION 4 + Task 13 handoff. Nice-to-haves a-e all folded (Localizer.Culture setter form; WPF wizard `App.Services` resolve; WPF-side tray test permitted-to-drop; `Stop()` cite fixed; Task 1 Step 6 HARD visual gate).
