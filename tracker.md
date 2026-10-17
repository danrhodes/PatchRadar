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
| P1 Core | Urgent + Important | In progress (P1.0, P1.1 done) |
| P2 Complete product | Important, schedule | Not started |
| P3 Hardening | Urgent, low value | Not started |
| P4 Polish | Not urgent, not important | Not started |

Last updated: 2026-10-17 (spec written, SignPath added, no code yet).

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
- [ ] **P1.2 Settings.** `AppSettings` model, defaults, `SettingsStore` (atomic write, corrupt-file recovery, unknown-key preservation).
  Accept: round-trip, corrupt-file and default-fill tests pass.
- [ ] **P1.3 Installed update collection.** `WuaHistoryProvider`, `QfeProvider`, merger, filters (days, categories, regex, failed).
  Accept: lists updates on a real machine in under 3 s. Dedupe tests pass. Standard-user run logged.
- [ ] **P1.4 KB fixtures.** Save real HTML for 6 KBs plus KB5127648 into `Fixtures/kb/`.
  Accept: fixtures committed. Page structure notes added to Decisions.
- [ ] **P1.5 KB fetcher and cache.** Source A, rate limit, backoff, TTL cache.
  Accept: cache and backoff tests pass. Live fetch of KB5127648 result (success or block) logged.
- [ ] **P1.6 KB parser.** Improvements, Known issues, no-issues, Resolved, unknown-structure fallback.
  Accept: all fixture tests pass. KB5127648 yields the Authentication improvement.
- [ ] **P1.7 Severity engine.** Keywords, change phrases, BehaviourChange, four states.
  Accept: golden test (KB5127648 = Red) and all rule tests pass.
- [ ] **P1.8 Icon set.** Generator script. Placeholder-quality is acceptable now, final art in P2.
  Accept: `app.ico` and four tray icons render at 16 px.
- [ ] **P1.9 Tray and menu.** Single instance. Colour-coded menu. Scan now. Startup delay and interval scheduler. Exit.
  Accept: menu lists newest 15 updates with correct colours. Tray icon shows worst severity. No taskbar button.
- [ ] **P1.10 Detail window.** Known issues, workaround, behaviour changes, improvements, KB link.
  Accept: clicking KB5127648 shows the NTLMv1 change and a working KB link.
- [ ] **P1.11 CI release workflow.** Pre-release on push to `main`. Full release on manual run (patch/minor/major).
  Accept: push creates a pre-release asset. Manual run creates a full release. Old pre-releases pruned to 10. Each exe has a `.sha256` asset.

## P2 â€” Complete product (schedule)

- [ ] **P2.1 Settings window.** All sections from guide section 7.5. Reset to defaults. Open settings file.
  Accept: every key in `settings.json` editable. Changes apply without restart.
- [ ] **P2.2 Dark mode.** System/Light/Dark. Live switch on OS theme change.
  Accept: menu, windows and grid correct in both themes.
- [ ] **P2.3 All Updates window.** Filters, symptom search over cached text, double-click to detail.
  Accept: searching `wifi` or `NTLM` returns KB5127648.
- [ ] **P2.4 Export.** CSV, JSON, Markdown. Respects filters. Also reachable from tray menu.
  Accept: Markdown output pastes cleanly into a ticket.
- [ ] **P2.5 Final icon art.** Radar concept per guide section 10.
  Accept: readable at 16, 24, 32 px on light and dark taskbars.
- [ ] **P2.6 Release health fallback (source B).** Enrich and fall back by originating KB.
  Accept: with source A blocked, a known issue still resolves for a test KB.
- [ ] **P2.7 Toast on new red and tray state.** Toast fires once per KB per content hash.
  Accept: no repeat toasts after restart.
- [ ] **P2.8 Run at login, logging.** HKCU Run toggle. Rolling logs, 14 days.
  Accept: toggle adds and removes the registry value. Log folder opens from menu.
- [ ] **P2.9 App auto-update.** Per guide section 11.1: GitHub Releases check, SemVer compare (pre-release N numeric), full/slim asset pick, SHA-256 verify, URL allowlist, rename-and-replace apply, `--post-update` restart, health check, rollback. Modes Off/Notify/Auto. Stable/PreRelease channels. Settings section and tray menu items.
  Accept: with a fake release newer than running, Auto mode downloads, verifies, swaps and restarts into the new exe. Hash mismatch aborts. Stable ignores pre-releases. Two failed starts roll back to `.old`. Unit tests pass with no network.
- [ ] **P2.10 Signing in CI (needs H3, H4).** Guide 11.2: zip, upload-artifact, SignPath action, unzip, `Get-AuthenticodeSignature` gate, checksum after signing, attest signed exes. Full releases only. Add `.signpath/policies/patchradar/release-signing.yml` and `docs/signpath/artifact-configuration.xml`.
  Accept: a manual full release produces signed exes. The job fails if the signature is not Valid or the signer is not SignPath Foundation. Pre-releases still publish unsigned.
- [ ] **P2.11 README code signing policy.** Attribution line, roles, verify command, privacy statement.
  Accept: README section matches guide 11.2 wording. Needed for H1 (can be done before approval).
- [ ] **P2.12 Updater signature check.** `IAuthenticodeVerifier` via `WinVerifyTrust` plus signer subject check. Constant `RequireSignatureOnStable` ships `false`. Flip to `true` after the first signed release verifies on a clean machine.
  Accept: tests cover valid, unsigned, wrong signer, tampered. PreRelease channel skips the check. Flip commit logged in Decisions.

## P3 â€” Hardening (urgent, low value; delegate or batch)

- [ ] **P3.1 Mark reviewed.** Flag per KB with content-hash change detection.
  Accept: edited known-issue text clears the flag and shows "changed since reviewed".
- [ ] **P3.2 Updater failure drills.** Corrupt download, hash mismatch, unsigned or wrong-signer exe on Stable, read-only install dir, new exe that crashes on start, AV blocking the rename, GitHub rate limit (403).
  Accept: each case logs a clear reason, leaves the running version intact and never leaves the install without a working exe.
- [ ] **P3.3 WebView2 fallback (source C).** Off by default. Setting to enable.
  Accept: renders a blocked page on a machine with the runtime. Graceful skip without it.
- [ ] **P3.4 OS test matrix.** Windows 11, Server 2016, 2019, 2022, 2025. Standard user and admin.
  Accept: matrix filled in below. Failures raised as issues.

## P4 â€” Polish (eliminate if time-poor)

- [ ] **P4.1 README.** Screenshots, install, settings reference, keyword tuning, FAQ.
- [ ] **P4.2 Menu tooltips.** First matched issue summary on hover.
- [ ] **P4.3 Provenance docs.** `gh attestation verify` instructions in README.

---

## Test matrix (P3.4)

| OS | Std user | Admin | Notes |
|---|---|---|---|
| Windows 11 | | | |
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
| 2026-10-17 | Keep `net10.0-windows`; test project also targets `net10.0-windows` so it can reference the WPF project | Server 2016 support not disproven. |

## Unverified items log

| Item | Result | Date |
|---|---|---|
| .NET 10 on Server 2016 | Inconclusive: web search found no Microsoft matrix; AWS Elastic Beanstalk runs .NET 10.0.11 on Server 2016, so it works in practice. Staying on net10.0-windows. Confirm on a real 2016 box in P3.4. | 2026-10-17 |
| `QueryHistory` as standard user | | |
| `support.microsoft.com` bot blocking from app | | |
| KB known-issues layout (table vs prose) | | |
| Release health page layout | | |
| Microsoft terms on automated access | | |
| Rename-running-exe update works on all matrix OSes | | |
| Defender / ThreatLocker reaction to self-replacing exe | | |
| SignPath Foundation approves a new repo (reputation check) | | |
| SignPath approval mode and typical wait | | |
| SignPath artifact size limit vs ~70 MB self-contained exe | | |
| SignPath action current major version and input names | | |
| `WinVerifyTrust` on Server 2016 with SHA-256 timestamped signature | | |

## Session log

| Date | Done | Next |
|---|---|---|
| 2026-10-16 | guide.md and tracker.md written | P1.0 |
| 2026-10-16 | Auto-update added to both files (guide 11.1, tracker P2.9, P3.2) | P1.0 |
| 2026-10-17 | SignPath signing added (guide 11.2, tracker H1â€“H4, P2.10â€“P2.12) | P1.0, Dan does H1 |
| 2026-10-17 | P1.0 scaffold, P1.1 TFM check | P1.2 |

