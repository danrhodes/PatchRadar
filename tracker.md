# PatchRadar â€” Tracker

Spec: `guide.md`. This file is the live progress record.

## Rules for Claude Code

1. Update this file after every task. Commit it with the code.
2. Tick `[x]` only when the acceptance test passes.
3. Phases run P1 â†’ P4. P1 = Urgent and Important. P4 = Not Urgent and Not Important.
4. Log every decision and every **unverified** result in the logs at the bottom.
5. Blocked? Add a line under **Blockers** with what is needed and from whom. Continue with the next unblocked task.

## Status

| Phase | Priority | State |
|---|---|---|
| P1 Core | Urgent + Important | In progress. P1.0-P1.10 done. P1.11 coded, awaiting CI verification. |
| P2 Complete product | Important, schedule | Code complete except P2.10 and P2.12, which wait on SignPath (H1-H4). |
| P3 Hardening | Urgent, low value | P3.1-P3.3 done. P3.4 needs real machines (use `tools/test-matrix.ps1`). |
| P4 Polish | Not urgent, not important | Done 2026-10-18. |
| P5 Advanced features | Requested 2026-10-18 | Done 2026-10-18 (uninstall needs one real-machine check). |

Last updated: 2026-10-18 (P2, P3.1-P3.3, P4 and P5 done; 386 offline tests pass).

## Human actions (Dan) â€” start now, lead time about 1 week (unverified)

- [ ] **H1** Apply to the SignPath Foundation OSS programme (Apply link on `signpath.org`).
- [ ] **H2** Enable MFA on GitHub and SignPath accounts.
- [ ] **H3** After approval: create project `patchradar`, policy `release-signing`, artifact configuration, GitHub trusted build system, Approver = Dan. See guide section 11.2.
- [ ] **H4** Add GitHub secret `SIGNPATH_API_TOKEN` and variable `SIGNPATH_ORGANIZATION_ID`.

## Blockers

- Full releases ship unsigned until H3 and H4 are done. Claude Code continues with P1 meanwhile.
- `RequireSignatureOnStable` stays `false` until the first signed release is verified (P2.12).

---

## P1 â€” Core (do now)

- [x] **P1.0 Scaffold.** `.sln`, `src/PatchRadar` (WPF), `tests/PatchRadar.Tests`, LICENSE (MIT), README stub, `.gitignore`, `Directory.Build.props` (nullable on, warnings visible).
  Accept: `dotnet build` and `dotnet test` pass on a clean clone.
- [x] **P1.1 Target framework check.** Confirm .NET 10 supports Windows Server 2016. If not, switch to `net8.0-windows`.
  Accept: result logged in Decisions.
- [x] **P1.2 Settings.** `AppSettings` model, defaults, `SettingsStore` (atomic write, corrupt-file recovery, unknown-key preservation).
  Accept: round-trip, corrupt-file and default-fill tests pass.
- [x] **P1.3 Installed update collection.** `WuaHistoryProvider`, `QfeProvider`, merger, filters (days, categories, regex, failed).
  Accept: lists updates on a real machine in under 3 s. Dedupe tests pass. Standard-user run logged.
- [x] **P1.4 KB fixtures.** Save real HTML for 6 KBs plus KB5127648 into `Fixtures/kb/`.
  Accept: fixtures committed. Page structure notes added to Decisions.
- [x] **P1.5 KB fetcher and cache.** Source A, rate limit, backoff, TTL cache.
  Accept: cache and backoff tests pass. Live fetch of KB5127648 result (success or block) logged.
- [x] **P1.6 KB parser.** Improvements, Known issues, no-issues, Resolved, unknown-structure fallback.
  Accept: all fixture tests pass. KB5127648 yields the Authentication improvement.
- [x] **P1.7 Severity engine.** Keywords, change phrases, BehaviourChange, four states.
  Accept: golden test (KB5127648 = Red) and all rule tests pass.
- [x] **P1.8 Icon set.** Generator script. Placeholder-quality is acceptable now, final art in P2.
  Accept: `app.ico` and four tray icons render at 16 px.
- [x] **P1.9 Tray and menu.** Single instance. Colour-coded menu. Scan now. Startup delay and interval scheduler. Exit.
  Accept: menu lists newest 15 updates with correct colours. Tray icon shows worst severity. No taskbar button.
  **Verified by Dan from screenshots 2026-10-17:** colour-coded menu with 15 updates, header shows last scan and next scan, no taskbar button. Grey rows are Defender, MSRT-style and QFE-only entries whose KB pages have no usable data.
- [x] **P1.10 Detail window.** Known issues, workaround, behaviour changes, improvements, KB link.
  Accept: clicking KB5127648 shows the NTLMv1 change and a working KB link.
  **Verified by Dan from screenshot 2026-10-17:** KB5127648 shows RED pill, keyword chips and known issues. "Mark reviewed" is deliberately left to P3.1.
- [ ] **P1.11 CI release workflow.** Pre-release on push to `main`. Full release on manual run (patch/minor/major).
  Accept: push creates a pre-release asset. Manual run creates a full release. Old pre-releases pruned to 10. Each exe has a `.sha256` asset.
  **Status: workflow written, NOT ticked.** Both publish commands succeed locally (self-contained and slim). Needs a push to `main` and a manual run on GitHub to verify.

## P2 â€” Complete product (schedule)

- [x] **P2.1 Settings window.** All sections from guide section 7.5. Reset to defaults. Open settings file.
  Accept: every key in `settings.json` editable. Changes apply without restart.
  **Done 2026-10-18:** all eight sections, every key of `settings.json` editable, Save validates and applies live (theme, scan timers, menu size, run at login, log level, update schedule, proxy/timeout/User-Agent). `network.maxConcurrent` applies after restart (stated in the UI). Layout checked from off-screen renders.

- [x] **P2.2 Dark mode.** System/Light/Dark. Live switch on OS theme change.
  Accept: menu, windows and grid correct in both themes.
  **Done 2026-10-18:** built on the WPF Fluent theme (`ThemeMode` System/Light/Dark, follows the OS live and via `UserPreferenceChanged`). Settings, All Updates (grid) and Detail windows and a ContextMenu rendered in both themes and checked. Dan: eyeball the real tray menu once.

- [x] **P2.3 All Updates window.** Filters, symptom search over cached text, double-click to detail.
  Accept: searching `wifi` or `NTLM` returns KB5127648.
  **Done 2026-10-18:** `wifi`, `Wi-Fi`, `NTLM`, `mschapv2`, `BlockNtlmv1SSO` all return KB5127648 (tests). The grid has no Reviewed column yet: it arrives with P3.1.

- [x] **P2.4 Export.** CSV, JSON, Markdown. Respects filters. Also reachable from tray menu.
  Accept: Markdown output pastes cleanly into a ticket.
  **Done 2026-10-18:** CSV (RFC 4180, formula-safe), JSON, Markdown table (pipes escaped, KB links). Save As and Copy as Markdown in the All Updates window (respects filters) and in the tray Export submenu (all scanned updates).

- [x] **P2.5 Final icon art.** Radar concept per guide section 10.
  Accept: readable at 16, 24, 32 px on light and dark taskbars.
  **Done 2026-10-18:** sweep wedge, crosshair, blip glow and a light rim so the tile reads on dark taskbars; the 16 px variant stays simple. See `assets/preview.png`.

- [x] **P2.6 Release health fallback (source B).** Enrich and fall back by originating KB.
  Accept: with source A blocked, a known issue still resolves for a test KB.
  **Done 2026-10-18:** `ReleaseHealthSource` is both fallback and enrichment. Test: source A returns 403, KB5124010 still resolves its Remote Desktop issue from release health and goes Red. Live `--diag` verified on this machine (26H2 page; KB5124010 gave 2 issues).

- [x] **P2.7 Toast on new red and tray state.** Toast fires once per KB per content hash.
  Accept: no repeat toasts after restart.
  **Done 2026-10-18:** `ToastTracker` persists KB + content hash in `state.json`; tests cover restart, changed hash, old updates recorded silently, corrupt file. The balloon itself is for Dan to eyeball on the next real red.

- [x] **P2.8 Run at login, logging.** HKCU Run toggle. Rolling logs, 14 days.
  Accept: toggle adds and removes the registry value. Log folder opens from menu.
  **Done 2026-10-18:** `Autostart` writes and removes `HKCU\...\Run\PatchRadar` (quoted path, repairs a stale path); real HKCU round-trip tested and seen on a real start. Rolling daily logs (keep 14) were done in P1; Open log folder is in the menu.

- [x] **P2.9 App auto-update.** Per guide section 11.1: GitHub Releases check, SemVer compare (pre-release N numeric), full/slim asset pick, SHA-256 verify, URL allowlist, rename-and-replace apply, `--post-update` restart, health check, rollback. Modes Off/Notify/Auto. Stable/PreRelease channels. Settings section and tray menu items.
  Accept: with a fake release newer than running, Auto mode downloads, verifies, swaps and restarts into the new exe. Hash mismatch aborts. Stable ignores pre-releases. Two failed starts roll back to `.old`. Unit tests pass with no network.
  **Done 2026-10-18:** all in `src/PatchRadar/AppUpdate/`, 100+ offline tests with fakes for HTTP, signature check, launcher and injectable file operations. Windows 11 build 26300 allows renaming the running exe (checked for real). Not yet exercised end to end against a real GitHub release (none exists until CI has run).

- [ ] **P2.10 Signing in CI (needs H3, H4).** Guide 11.2: zip, upload-artifact, SignPath action, unzip, `Get-AuthenticodeSignature` gate, checksum after signing, attest signed exes. Full releases only. Add `.signpath/policies/patchradar/release-signing.yml` and `docs/signpath/artifact-configuration.xml`.
  Accept: a manual full release produces signed exes. The job fails if the signature is not Valid or the signer is not SignPath Foundation. Pre-releases still publish unsigned.
  **Status: coded, NOT ticked (needs H3 and H4).** Signing steps are gated on `SIGNPATH_API_TOKEN` + `SIGNPATH_ORGANIZATION_ID`: without them a full release ships unsigned and its notes say so. Action inputs checked against `signpath/github-action-submit-signing-request@v2` `action.yml`. Files added: `.signpath/policies/patchradar/release-signing.yml`, `docs/signpath/artifact-configuration.xml`.

- [x] **P2.11 README code signing policy.** Attribution line, roles, verify command, privacy statement.
  Accept: README section matches guide 11.2 wording. Needed for H1 (can be done before approval).
  **Done 2026-10-18:** README has Code signing policy (attribution, roles, verify command, privacy), install location and integrity limits.

- [ ] **P2.12 Updater signature check.** `IAuthenticodeVerifier` via `WinVerifyTrust` plus signer subject check. Constant `RequireSignatureOnStable` ships `false`. Flip to `true` after the first signed release verifies on a clean machine.
  Accept: tests cover valid, unsigned, wrong signer, tampered. PreRelease channel skips the check. Flip commit logged in Decisions.
  **Status: coded, NOT ticked.** `IAuthenticodeVerifier`, `WinTrustVerifier` (WinVerifyTrust + signer subject) and the Stable-channel gate exist and are tested for valid, unsigned, wrong signer and tampered (including real files). `RequireSignatureOnStable` ships `false`. Tick after the flip commit.


## P3 â€” Hardening (urgent, low value; delegate or batch)

- [x] **P3.1 Mark reviewed.** Flag per KB with content-hash change detection.
  Accept: edited known-issue text clears the flag and shows "changed since reviewed".
  **Done 2026-10-18:** `ReviewStore` keeps KB + content hash + time in `state.json` (shared, atomic `StateStore.Update` with the toast tracker). Test: editing a cached known-issue text flips Reviewed to "changed since reviewed" until it is reviewed again. Detail window has Mark reviewed / Mark not reviewed and a "changed since reviewed" banner; tray menu shows a tick and dims reviewed items (`dimReviewed`) and tags changed ones; All Updates has a Reviewed column and filter; exports carry the state. Only articles that were fetched can be marked. Reviewing does not change severity.
- [x] **P3.2 Updater failure drills.** Corrupt download, hash mismatch, unsigned or wrong-signer exe on Stable, read-only install dir, new exe that crashes on start, AV blocking the rename, GitHub rate limit (403).
  Accept: each case logs a clear reason, leaves the running version intact and never leaves the install without a working exe.
  **Done 2026-10-18:** 25 drills in `UpdaterFailureDrillTests`: truncated and dropped downloads, hash mismatch, stale tampered cache, unsigned / wrong signer / tampered on Stable, read-only install dir (at check time and at apply time), new exe that never stays up, AV blocking the rename or quarantining mid-copy, pending.json unwritable, restore blocked (falls back to copy; double fault is logged CRITICAL with the path of the good copy), staged file quarantined, updates folder unwritable, new exe cannot be started, GitHub 403 and 429 with rate-limit reset, 502, hostile asset hosts, recovery on the next check. Each asserts a logged reason and that the old exe is still in place. The drills found and fixed three gaps (see Decisions).
- [x] **P3.3 WebView2 fallback (source C).** Off by default. Setting to enable.
  Accept: renders a blocked page on a machine with the runtime. Graceful skip without it.
  **Done 2026-10-18:** `WebViewSource` + `WebView2Renderer` (hidden off-screen window, navigation limited to https `*.microsoft.com`, downloads and pop-ups refused). Off by default. Verified live on this machine (runtime 155.0): KB5127648 rendered through WebView2 gives the same 12 improvements, 3 issues, Red as source A; a true 404 returns NotFound in about 3 s. A missing runtime (simulated with `WEBVIEW2_BROWSER_EXECUTABLE_FOLDER` pointing at an empty folder) reports unavailable and the chain skips it. The page was not actually blocked on this machine, so the challenge-solving path is covered by the fake-renderer tests only.
- [ ] **P3.4 OS test matrix.** Windows 11, Server 2016, 2019, 2022, 2025. Standard user and admin.
  Accept: matrix filled in below. Failures raised as issues.
  **Status: NOT ticked (needs real machines).** `tools/test-matrix.ps1 -Exe <PatchRadar exe>` runs on each box and prints the row to paste: OS and elevation, `--diag` (WUA, QFE, merge time, live KB fetch, release health page), a real rename-the-running-exe drill, WinVerifyTrust via the new `--verify <out> <file>` mode, installed .NET runtimes. Needed: Windows 11 admin, Server 2016, 2019, 2022, 2025, each as standard user and admin.

## P4 â€” Polish (eliminate if time-poor)

- [x] **P4.1 README.** Screenshots, install, settings reference, keyword tuning, FAQ.
  **Done 2026-10-18:** full README written as if published: features, install, tray and detail-window guide, colour meanings, how risk is decided, full settings reference, keyword tuning, app updates and integrity, code signing policy, privacy, FAQ, build from source, contributing. Screenshots in `docs/images/` (light and dark, shown with `prefers-color-scheme`), rendered off-screen from the real windows with sample data from the saved KB fixtures. Regenerate with the render harness if the UI changes.
- [x] **P4.2 Menu tooltips.** First matched issue summary on hover.
  **Done 2026-10-18:** menu rows show the first matched issue or behaviour change as a tooltip (from P1.9); verified in code. Rows now also carry the reviewed tick and "changed since reviewed" tag.
- [x] **P4.3 Provenance docs.** `gh attestation verify` instructions in README.
  **Done 2026-10-18:** README section "Provenance and integrity" has the `gh attestation verify` command and the limits of the `.sha256` file.

---

## P5 — Advanced features (added 2026-10-18 at Dan's request)

- [x] **P5.1 Fix available, not installed.** Flag when a named fix KB (including out-of-band and partial fixes, and issues Microsoft calls resolved) is not installed on this PC.
  Accept: tray row, detail chip, At a glance line and a grid column + filter show it; a newer cumulative update adds a "probably already includes it" note. **Done:** `UpdateRow.Fixes`, tests in `ReviewBaselineAndFixTests`, rendered and checked.
- [x] **P5.2 Rank by keyword matches.** **Done:** Hits column, "Rank by keyword matches" (hits, then risk, then date), exports carry hits and fixes not installed.
- [x] **P5.3 Before and after for a changed article.** **Done:** `KbDiff` compares text (not hashes); reviewed items keep a snapshot in `cache\reviewed`; every fetch keeps `Previous` + `ChangedUtc`. Detail window lists added/removed/changed with BEFORE and AFTER. A parser upgrade that changes hashes but no text is not reported as an edit (tested).
- [x] **P5.4 What's changed since I last looked.** **Done:** `DigestBuilder` (new updates, edited articles, became riskier, fix now named, reviewed-then-changed), `DigestStore` (last seen, default a week back), window with Open details and Mark all as seen, count badge in the tray menu.
- [x] **P5.5 Uninstall KB (opt-in, administrators only).** **Done, never executed for real in testing.** Setting `advanced.enableUninstall` (default off). Button only when the feature is on, the user is an administrator (UAC-limited admins included), and `UninstallPlanner` says removal is possible: newest cumulative update via `dism /online /remove-package`, standalone MSU packages via `wusa /uninstall`; never servicing stack, Defender/MSRT, drivers, Office, modern .NET, older (superseded) cumulative updates, or anything without a removable package. Warning dialog with all risks, exact command, optional reason, acknowledgement and type-the-KB confirmation. Audit JSONL (Started + outcome, time UTC and local, `DOMAIN\user`, machine, KB, method, command, exit code, reason); if the audit write fails nothing runs. 100 tests (planner table, results mapping, privilege classification, gating, audit, confirmation). Read-only check on this machine: only KB5127648 (newest cumulative) is offered, with its real package name.
  Still to do by a human: one real removal on a test VM (Windows 11 and a Server), to confirm the DISM and wusa exit codes and the UAC flow. See Unverified items.

## Test matrix (P3.4)

| OS | Std user | Admin | Notes |
|---|---|---|---|
| Windows 11 | PASS 2026-10-18: WUA 344 entries 112 ms, merged list 554 ms, live KB fetch Ok, rename of a running exe OK, WinVerifyTrust OK | | Build 26300 (26H2), `tools/test-matrix.ps1`. Admin run still to do. |
| Server 2016 | | | |
| Server 2019 | | | |
| Server 2022 | | | |
| Server 2025 | | | |

## Decisions

| Date | Decision | Reason |
|---|---|---|
| 2026-10-16 | Name PatchRadar | Fits Ticktower air-traffic family. Radar icon. |
| 2026-10-16 | Target `net10.0-windows`, fall back to `net8.0-windows` | .NET 8 EOL 10 Nov 2026. Server 2016 support unverified. |
| 2026-10-16 | Scrape Improvements and Known issues | No Microsoft API for KB known issues. |
| 2026-10-16 | Behaviour-change detection on Improvements | KB5127648 breaking change sits under Improvements. |
| 2026-10-16 | Four states incl. Grey | Unfetched or blocked KBs must not show Green. |
| 2026-10-16 | Auto-update from `danrhodes/PatchRadar` GitHub Releases, default `Auto`, Stable channel | Requested. Per-user, in-process rename-and-replace, no elevation. |
| 2026-10-17 | Code signing via SignPath Foundation. Full releases signed, pre-releases unsigned. | Free for OSS. Let's Encrypt cannot sign code. Per-push signing would block on manual approval. |
| 2026-10-17 | KB page structure (from fixtures): Improvements are `<ul><li><strong>[Category]</strong> text`. Known issues come in three layouts: (a) table with Symptom/Workaround columns, optional Applies to column; (b) `<details><summary>Title</summary>` with bold paragraphs Symptoms / Resolution; (c) `<details>` with tab sections `data-tab="symptom"` and `data-tab="workaround"`. No issues = "not currently aware of any issues". .NET Framework pages use "Summary" not "Improvements". | Fixtures decide (P1.4). Parser handles all three. |
| 2026-10-17 | Issue counts as Resolved only on "this/the issue has been resolved" or a leading "Resolved". "Partially resolved" stays Active. | KB5127648 USB audio issue is only partially fixed. |
| 2026-10-17 | Matched keywords (chips) come from active issues and behaviour changes only, not every improvement. | Improvements mention many unrelated keywords; chips would be noise. |
| 2026-10-17 | HTTP 404 from the KB page = status NotFound ("Microsoft has no KB article for this update"). Retry in 7 days, not 1/6/24 h. A 200 page with an h1 but no KB sections (e.g. KB5007651 Windows Security app update) is a real article: Status Ok, Structure Unknown, no longer mislabelled as a bot block. | Seen on KB5127647, KB4052623 (404) and KB5007651. Defender platform/MSRT-style updates still show Grey; consider adding them to `excludeTitleRegex` defaults (not done, guide lists only two patterns). |
| 2026-10-17 | Full (self-contained) exe built with `EnableCompressionInSingleFile=true`. | Uncompressed it was 134.5 MB; compressed 62.5 MB. Slightly slower first start. Trimming unsupported for WPF. Helps the SignPath size risk. |
| 2026-10-17 | `--diag <file> [KB...]` command-line mode added. Headless: times update collection, live-fetches KBs. | Gives acceptance evidence without a UI. |
| 2026-10-17 | Keep `net10.0-windows`; test project also targets `net10.0-windows` so it can reference the WPF project | Server 2016 support not disproven. |
| 2026-10-18 | P2 started while P1.11 was still unverified (CI never run). | Dan asked to continue; P1.11 stays open. The first push to `main` is the real test of the workflow. |
| 2026-10-18 | Theme via the WPF Fluent theme (`Application.ThemeMode`) rather than hand-written resource dictionaries. | One switch styles every control, the menu, the DataGrid and the window chrome, and follows the OS live. Needs `WPF0001` suppressed (experimental API). |
| 2026-10-18 | Settings window edits a clone of the live settings; keyword boxes show the effective list and store an empty list when it equals the defaults. | Keeps unknown keys, and future default changes still apply. |
| 2026-10-18 | Release health pages are chosen from the local OS (registry build + DisplayVersion) and issues matched by originating KB. Status page slugs verified live (Windows 11 21H2 to 26H2, Server 2016 to 2025). | The installed update is on this machine, so only its own OS page matters. A Resolved row stays Resolved even if the resolving KB is not installed; the text names the resolving KB. |
| 2026-10-18 | When every source fails, `LastError` lists each source's error (for example `support.microsoft.com: HTTP 403; learn.microsoft.com (release health): ...`); retry timing follows the first source. | A 404 on source A still means a 7 day retry even when B has nothing either. |
| 2026-10-18 | Toasts: one per KB per content hash, persisted in `state.json`. Updates older than `recentWindowDays` are recorded silently; several new reds are grouped into one toast. Recorded even when toasts are off. | A first run must not toast the whole history, and switching toasts on later must not replay old reds. |
| 2026-10-18 | Updater health rule read literally: a start that finds `attempts >= 2` and `healthy: false` rolls back; every earlier start increments. So the third start after an update performs the rollback. The failing exe is kept as `.failed`, not deleted. | Guide 11.1. One crash does not trigger a rollback; two do. |
| 2026-10-18 | Off mode: no automatic checks, but the tray's manual Check for app update still works and behaves as Notify. Development builds (version `1.0.0`, no `-p:Version`) skip automatic checks and never auto-apply. | An explicit click is a request; a dev build must not overwrite `bin\Debug`. |
| 2026-10-18 | Signature check uses WinVerifyTrust with revocation lookups off, then requires `O=SignPath Foundation` in the signer subject. | Servers that cannot reach CRL endpoints would otherwise reject good files; the chain, hash and signer subject checks remain. |
| 2026-10-18 | CI uploads the two exes directly with `upload-artifact` (the GitHub artifact is already a ZIP) instead of pre-zipping them as the guide worded it; artifact configuration root is `zip-file`. Signing is skipped, with a warning and a note in the release body, when the SignPath secret or variable is missing. | A zip inside the artifact zip would need nested configuration. The gate keeps full releases possible before H3 and H4. |
| 2026-10-18 | `.signpath/policies/patchradar/release-signing.yml` uses SignPath's documented pipeline-policy keys (`github-build-policies`: `disallow_reruns`, `runners.require_github_hosted`). The docs show no repo, workflow or branch keys, so that restriction is set in SignPath (trusted build system link), not in the file. |
| 2026-10-18 | Review flag stores the KB content hash (improvements plus known issues). A different hash on a later fetch shows "changed since reviewed" until re-reviewed. A review whose fetch failed this time keeps showing Reviewed. Only fetched (Ok) articles can be reviewed. | Guide section 9 and P3.1. Reviewing is a note to self and does not change severity. |
| 2026-10-18 | `StateStore.Update` does load, change and save under one lock; the toast tracker and review store both use it. | Two writers on one `state.json` must not overwrite each other with a stale copy. |
| 2026-10-18 | P3.2 drills found and fixed three gaps: (1) an `IOException` or `UnauthorizedAccessException` while storing or reading the download escaped `CheckAsync` (now a Failed outcome with the folder named); (2) a failure to start the new exe after the swap threw (now an Applied outcome telling the user to restart manually; pending.json stays so the health check still runs); (3) if the restore rename was blocked the installer gave up (now it retries by copy before logging CRITICAL). | The invariant "never leave the install without a working exe". |
| 2026-10-18 | Fetch chain order is now: support site, WebView2 (opt-in), release health. Enrichment from release health applies to any full article, from source A or WebView2. WebView2 reports HTTP status, and 404 maps to NotFound like source A. Package `Microsoft.Web.WebView2` 1.0.4258.31. | WebView2 gives the whole article; release health only gives known issues, so it should come last. |
| 2026-10-18 | `--verify <outfile> <exe>` prints the Authenticode verdict via the same `WinTrustVerifier` the updater uses. | Lets the first signed release be checked on a clean machine before `RequireSignatureOnStable` is flipped (P2.12), and feeds the OS matrix. |
| 2026-10-18 | Observed, not changed: `support.microsoft.com/help/1` redirects to a learn.microsoft.com landing page with status 200, so a bogus KB number parses as an Ok article with unknown structure (same in source A and WebView2). Real KB numbers are unaffected. | Noted for P4 if it ever matters. | Guide 11.2 asked for those restrictions in the file; the format does not support them. |
| 2026-10-18 | Known issues now carry separate Symptom, Workaround, Resolution and Next steps text plus a structured fix: `ResolvedInKb`, `OutOfBandFix`, `PartialFix`. Parsed from the article labels and from "addressed / resolved in KB..." wording; release health rows fill `ResolvedInKb` too. The detail window leads with an At a glance panel and shows each issue as a collapsible card with a tag (ACTIVE, WORKAROUND, FIX AVAILABLE, PARTIAL FIX, RESOLVED), the fix as a link, and whether the fix KB is installed on this PC. | Dan's screenshot: Workaround and Resolution were one wall of text and nothing said whether a fix existed. Microsoft's own "Resolution" heading sometimes holds mitigation guidance, so it is labelled "per Microsoft" and only a named update counts as a Fix. |
| 2026-10-18 | About is a real window, and every tray menu action runs after the popup has closed (`ApplicationIdle`). | A `MessageBox` opened inside the menu click was dismissed with the popup. Export save dialogs used the same path. |
| 2026-10-18 | Uninstalling updates from PatchRadar is NOT built. Guide section 14 lists it as a non-goal. Design notes in the session reply: opt-in setting, elevation on demand, `wusa /uninstall` cannot remove most cumulative updates (needs `DISM /Remove-Package`), confirmation, and never offered for the newest servicing stack. | Waiting for Dan's decision. |
| 2026-10-18 | Uninstall is built (guide section 14 amended). Method per type: newest cumulative or security update by DISM `/remove-package` (wusa cannot remove combined SSU+LCU packages); standalone MSU packages by `wusa /uninstall /kb:`; nothing for servicing stack, Defender/MSRT, drivers, Office, modern .NET, or superseded cumulative updates. The plan comes from Component Based Servicing in the registry (readable without elevation; state 112 = installed, one installed `Package_for_RollupFix` is the newest), so the button is decided before any prompt. | "Use the best method for each type" and "button only where removal is actually possible". |
| 2026-10-18 | Admin check is "in the Administrators role OR token elevation type Limited", because a UAC admin running a desktop app has the group as deny-only. The app is not elevated: each removal asks for UAC (`runas`). A standard user sees nothing, and the service refuses to execute for them even if called directly. | Dan: standard users must not see the option. |
| 2026-10-18 | Every removal writes a Started record before running and an outcome record after (UTC and local time, `DOMAIN\user`, machine, KB, method, exact command, exit code, reason). If the Started record cannot be written, nothing runs. The file is a local, not tamper-proof record; forwarding to a SIEM is the answer for an immutable trail. | Dan: record time, date and user. |
| 2026-10-18 | The confirmation needs a tick plus the typed KB number; Cancel is the default; the buttons are docked outside the scroll area. Warnings are generated from data (`UninstallConfirmation.Warnings`) and tested. | A casual click must not remove a security update. |
| 2026-10-18 | Review baselines are content snapshots stored per reviewed KB in `%LOCALAPPDATA%\PatchRadar\cache\reviewed`. "Changed since reviewed" is decided by comparing text; a different hash with identical text stays Reviewed. | The hash changes whenever the parser gains fields (it did today); that must not flip every reviewed update. |
| 2026-10-18 | Digest "last looked" = the last Mark all as seen, defaulting to 7 days back the first time. Article history is one level deep (`Previous`). | Keeps state small; enough for "what changed lately". |

## Unverified items log

| Item | Result | Date |
|---|---|---|
| .NET 10 on Server 2016 | Inconclusive: web search found no Microsoft matrix; AWS Elastic Beanstalk runs .NET 10.0.11 on Server 2016, so it works in practice. Staying on net10.0-windows. Confirm on a real 2016 box in P3.4. | 2026-10-17 |
| `QueryHistory` as standard user | Works. `--diag` as non-elevated user: WUA 343 entries in 209 ms, QFE 6 in 1.4 s, merged list 944 ms total. Win11 26300. | 2026-10-17 |
| `support.microsoft.com` bot blocking from app | Not blocked. Live fetch of KB5127648 and KB5044281 succeeded with the browser User-Agent, 0.7 to 1.8 s each. One machine, one day. Keep watching. | 2026-10-17 |
| KB known-issues layout (table vs prose) | All three layouts occur. See Decisions. | 2026-10-17 |
| Release health page layout | Known issues table: columns Summary / Originating update / Status / Last updated; title in a bold link, resolving KB in the Status cell. Parsed by header text. All 8 status-page URLs return 200. One page live-checked on 26H2. | 2026-10-18 |
| Microsoft terms on automated access | | |
| Rename-running-exe update works on all matrix OSes | Windows 11 26300: renaming and replacing a running exe works; deleting it is blocked (as designed). Other matrix OSes still to do in P3.4. | 2026-10-18 |
| Defender / ThreatLocker reaction to self-replacing exe | | |
| SignPath Foundation approves a new repo (reputation check) | | |
| SignPath approval mode and typical wait | | |
| SignPath artifact size limit vs ~70 MB self-contained exe | | |
| SignPath action current major version and input names | `signpath/github-action-submit-signing-request` v2: inputs `api-token`, `organization-id`, `project-slug`, `signing-policy-slug`, `artifact-configuration-slug`, `github-artifact-id`, `wait-for-completion(-timeout-in-seconds)`, `output-artifact-directory` confirmed in its action.yml. | 2026-10-18 |
| `WinVerifyTrust` on Server 2016 with SHA-256 timestamped signature | | |
| Real uninstall on a test machine (DISM remove-package for the newest cumulative update; wusa for a standalone MSU) | Not run. Planner verified read-only on Win11 26300; commands and exit-code mapping come from documentation. Needs a VM: confirm the UAC prompt, the exit codes, and that the update leaves Installed updates after a restart. | |

## Session log

| Date | Done | Next |
|---|---|---|
| 2026-10-16 | guide.md and tracker.md written | P1.0 |
| 2026-10-16 | Auto-update added to both files (guide 11.1, tracker P2.9, P3.2) | P1.0 |
| 2026-10-17 | SignPath signing added (guide 11.2, tracker H1â€“H4, P2.10â€“P2.12) | P1.0, Dan does H1 |
| 2026-10-17 | P1.0 scaffold, P1.1 TFM check | P1.2 |
| 2026-10-17 | P1.2-P1.8 done and ticked. P1.9, P1.10, P1.11 coded, awaiting manual or CI check. 74 tests pass. | Dan: run the app and check the tray. Push to verify CI. Then P2. |
| 2026-10-18 | P2.1-P2.9, P2.11 done and ticked; P2.10 and P2.12 coded and waiting on SignPath. 231 tests pass. | Dan: eyeball the tray menu and a real toast; push to verify CI (P1.11). Then P3. |
| 2026-10-18 | P3.1-P3.3 done and ticked (287 tests). P3.4 helper script written; Windows 11 standard-user row filled. | Dan: run `tools/test-matrix.ps1` on the Servers and an elevated Windows 11 for P3.4; push to verify CI (P1.11); do H1-H4. |
| 2026-10-18 | P4 done. Detail window redesigned (fix, workaround, resolution, installed check). About window fixed. 290 tests. | Dan: P1.11 CI result, H1-H4, P3.4 matrix, decide on uninstall-from-app. |
| 2026-10-18 | P5 done: fix-not-installed flag, keyword ranking, before/after diff, what's-changed digest, opt-in audited uninstall. 386 tests. | Dan: test one real uninstall on a VM; push; watch CI; H1-H4; P3.4 matrix. |
