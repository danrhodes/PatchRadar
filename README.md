# PatchRadar

**A tray tool that tells you which Windows update broke it.**

PatchRadar sits in the notification area, lists the updates installed on the machine, reads each update's Microsoft KB article, and colours it by risk. It pulls out what Microsoft buries: **known issues**, **workarounds**, **fixes and out-of-band updates**, and **behaviour changes hidden under "Improvements"**.

<p align="center">
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/tray-menu-dark.png">
  <img src="docs/images/tray-menu-light.png" alt="PatchRadar tray menu: installed updates coloured red, green or grey by risk" width="560">
</picture>
</p>

It was built for the moment an engineer sees a symptom on a machine ("Wi-Fi 802.1X stopped working", "RDP fails for standard users") and needs to know **which update did it, and what to do about it**.

## Why it exists

In October 2026 KB5127648 changed the `BlockNtlmv1SSO` default from Audit to Enforce. That silently broke MS-CHAPv2 based Wi-Fi (802.1X/RADIUS), Ethernet and VPN. Microsoft listed it under **Improvements**, not **Known issues**. A tool that only reads the Known issues section shows that update as safe.

PatchRadar flags it red, because an improvement that matches a *change phrase* ("changing the default", "enforce mode", "will no longer") **and** a *critical keyword* ("NTLMv1", "MS-CHAPv2", "Wi-Fi", "VPN") is treated as a **behaviour change**.

## Features

- **Colour-coded tray icon and menu.** Green, orange, red or grey for the newest updates. The tray icon shows the worst one.
- **Known issues with what to do next.** Every issue is a collapsible card with clearly labelled **Symptom**, **Workaround**, **Resolution** and **Fix** sections, plus an **At a glance** summary: how many issues are active, which have a fix or workaround, and which have *nothing published yet*.
- **Fix tracking.** When an article or Windows release health names the update that fixes an issue (including **out-of-band** and **partial** fixes), PatchRadar links it and tells you whether that update is **already installed on this PC**.
- **Behaviour-change detection** in the Improvements section.
- **Symptom search.** Type `wifi`, `NTLM` or `0x80070` and see which updates mention it.
- **Mark reviewed.** Flag an update as dealt with. If Microsoft later edits the article, PatchRadar shows **changed since reviewed**.
- **Export** the list as CSV, JSON or a Markdown table that pastes cleanly into a ticket. **Copy summary** puts one update's findings on the clipboard.
- **Notifications** for new red updates, once per update per article revision.
- **Light, dark or follow-the-system theme**, and every threshold, keyword and colour is configurable.
- **Self-updating** from GitHub Releases, with checksum verification and automatic rollback.
- **No telemetry, no service, no admin rights.**

## Screenshots

### An update's detail window

Active issues open by default. Each says whether there is a fix, a workaround or nothing yet, and whether the fix is installed here.

<p align="center">
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/detail-dark.png">
  <img src="docs/images/detail-light.png" alt="Detail window for KB5127648 with an At a glance summary and collapsible known issue cards" width="520">
</picture>
</p>

### Symptom search

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/symptom-search-dark.png">
  <img src="docs/images/symptom-search-light.png" alt="All updates window filtered by the symptom wifi" width="100%">
</picture>

### All updates

Filter by risk, category, review state and date. Export respects the filter.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/all-updates-dark.png">
  <img src="docs/images/all-updates-light.png" alt="All updates window listing every update with risk, KB, title and notes" width="100%">
</picture>

### Settings

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/settings-dark.png">
  <img src="docs/images/settings-light.png" alt="Settings window" width="620">
</picture>

## Requirements

- Windows 11, or Windows Server 2016, 2019, 2022, 2025 and later. Windows 10 is not supported.
- No admin rights. Reading update history works as a standard user.
- Internet access to `support.microsoft.com` and `learn.microsoft.com` (see [Privacy](#privacy)).
- Nothing else to install: the main download is self-contained.

## Install

1. Download the latest **full release** from the [Releases page](https://github.com/danrhodes/PatchRadar/releases):
   - `PatchRadar-<version>-win-x64.exe`: self-contained. Use this on Servers and anywhere you are unsure.
   - `PatchRadar-<version>-win-x64-slim.exe`: needs the .NET 10 Desktop Runtime. Smaller.
2. Put it in a per-user folder, for example `%LOCALAPPDATA%\Programs\PatchRadar\PatchRadar.exe`.
3. Run it. An icon appears in the notification area (it may be in the overflow arrow). By default PatchRadar starts at login; turn that off in **Settings, General**.

Why per-user: the folder is writable without elevation, so the built-in updater can replace the exe in place. In `Program Files` the updater cannot write, so it falls back to telling you a new version exists.

**Verify your download** (see [Code signing policy](#code-signing-policy) and [Provenance](#provenance-and-integrity)):

```powershell
Get-FileHash .\PatchRadar-v1.0.0-win-x64.exe -Algorithm SHA256   # compare with the .sha256 asset
Get-AuthenticodeSignature .\PatchRadar-v1.0.0-win-x64.exe        # signed releases only
```

If your antivirus or EDR (Defender, ThreatLocker and similar) objects to an exe that renames and replaces itself, allow `PatchRadar.exe` in its install folder. See [App updates](#app-updates).

## Using PatchRadar

### The tray

Left- or right-click the icon (left-click is configurable). Top to bottom:

| Item | What it does |
|---|---|
| Header | Time of the last scan and the next one. Shows `Scanning… (3/15)` while working. |
| Update rows | The newest updates (15 by default), newest first. Dot and text are coloured by risk. A tick and dimmed text mean you marked it reviewed. Hover for the first matched issue. Click to open the detail window. |
| Scan now | Rescans and refreshes updates inside the recent window. |
| View all updates… | The full list with filters and symptom search. |
| Export… | CSV, JSON, Markdown, or copy as Markdown. |
| Settings… | Every setting. Changes apply immediately. |
| Open log folder | Opens `%LOCALAPPDATA%\PatchRadar\logs`. |
| Check for app update | Becomes **Restart to update to vX.Y.Z** when an update is staged. |
| About, Exit | |

The icon takes the worst colour of the updates in the menu. While a scan runs it is grey.

### What the colours mean

| Colour | Meaning |
|---|---|
| 🟢 **Green** | The article was read. No active known issues and no behaviour change involving a critical keyword. |
| 🟠 **Orange** | At least one active known issue, but nothing matching a critical keyword. |
| 🔴 **Red** | An active known issue, or a behaviour change, that matches a critical keyword (authentication, VPN, BitLocker, RDP, boot, print and so on). |
| ⚪ **Grey** | No data: not fetched yet, the fetch failed, or Microsoft has no article for it. Common for Defender definition, MSRT and some driver updates. |

**Resolved issues never count.** "Partially resolved" is still active.

### The detail window

Click any update. From the top:

1. **Header**: KB, title, install date and result, OS build, the risk pill and the keywords that matched.
2. **At a glance**: the active-issue count, which issues have a fix (and whether it is installed here), which have a workaround, and which have **no fix or workaround yet**.
3. **Known issues**: one card per issue.
   - A tag says where it stands: `ACTIVE` (no fix or workaround), `WORKAROUND`, `FIX AVAILABLE`, `PARTIAL FIX`, `RESOLVED`.
   - Inside: **Symptom**, then **Fix** (a link to the fixing update, with `out-of-band` and `installed on this PC` / `not installed` markers), **Workaround**, **Resolution (per Microsoft)** and **Next steps**.
   - Long guidance (registry steps, policy walkthroughs) shows a short preview with **Show full text**, so the window stays scannable.
   - Resolved issues are collapsed and greyed.
4. **Behaviour changes**: improvements flagged as risky, highlighted.
5. **Improvements**: the full list, collapsed.

Buttons: **Mark reviewed**, **Open KB article** (your browser), **Copy summary** (plain text for ticket notes), **Refresh**.

> "Resolution (per Microsoft)" is the heading Microsoft uses. In practice it is sometimes guidance (a registry change, a policy) rather than a fix. A **Fix** line only appears when an update is actually named.

### Finding the update behind a symptom

Open **View all updates…** and use **Symptom search**. It searches the cached improvements, known issues and workarounds of every update. It ignores case and punctuation, so `wifi` finds "Wi-Fi" and `mschapv2` finds "MS-CHAPv2". Hex codes like `0x80070` work as typed. Combine it with the risk, category, review and date filters.

### Reviewing

**Mark reviewed** records the article's content hash. If Microsoft edits the article (a new workaround, a new known issue), the update shows **Changed since reviewed** in the menu, the grid and the detail window until you review it again. Reviewing never changes the risk colour; it is a note to yourself.

### Exporting

From the tray (all scanned updates) or the All Updates window (the **filtered** list): CSV (opens cleanly in Excel), JSON (full detail), or a Markdown table with KB links. **Copy as Markdown** is the fastest route into a ticket.

## How the risk is decided

1. The KB article is fetched from `support.microsoft.com` and parsed **by heading text**, never by CSS class, so Microsoft page redesigns rarely break it.
2. **Known issues** are read from tables, expandable sections or plain paragraphs. If the structure is unrecognised, the raw text is kept; data is never dropped.
3. **Improvements** that contain a *change phrase* and a *critical keyword* become **behaviour changes**.
4. Active issues and behaviour changes are matched against the **critical keywords** (word-boundary, case-insensitive).
5. Fix information comes from the article text ("addressed in KB…", "resolved in the out-of-band update KB…") and from the Windows **release health** page for your OS, matched by the update that introduced the issue.

If the support site blocks a request, PatchRadar falls back to release health (known issues only) and, if you enable it, a hidden browser render of the real article. Fetches are rate-limited (two at a time, 500 ms apart), cached, and retried with back-off.

## Settings reference

Settings live in `%APPDATA%\PatchRadar\settings.json`. Use the Settings window, or edit the file; **Open settings file** is in the window. Unknown keys are kept, invalid values fall back to defaults with a log warning, and a corrupt file is backed up as `settings.json.bad`.

| Section | Key | Default | Meaning |
|---|---|---|---|
| general | `runAtLogin` | `true` | Start at login (HKCU Run). |
| | `leftClickOpensMenu` | `true` | Otherwise right-click only. |
| | `toastOnNewRed` | `true` | Notify once per red update per article revision. |
| scanning | `scanOnStartup` | `true` | |
| | `startupDelayMinutes` | `30` | `0` scans immediately. |
| | `scanIntervalMinutes` | `360` | `0` disables periodic scans. |
| | `cacheHoursRecent` | `12` | Refresh interval for updates inside the recent window. |
| | `cacheDaysOlder` | `7` | Refresh interval for older updates. |
| | `recentWindowDays` | `30` | What counts as recent. |
| updates | `menuItemCount` | `15` | Rows in the tray menu (5 to 100). |
| | `historyDays` | `90` | History window. `0` is all time. |
| | `includeCategories` | all except Definition | Security, Cumulative, DotNet, ServicingStack, Driver, Office, Other. |
| | `excludeTitleRegex` | Defender definition patterns | Titles matching are hidden. |
| | `showFailed` | `false` | Show failed installs. |
| severity | `criticalKeywords` | `[]` (built-ins) | See below. |
| | `changePhrases` | `[]` (built-ins) | See below. |
| | `treatBehaviourChangesAsIssues` | `true` | Turn off for "known issues only" behaviour. |
| | `dimReviewed` | `true` | Dim reviewed rows in the menu. |
| appearance | `theme` | `System` | `System`, `Light`, `Dark`. Follows the OS live. |
| | `colors` | green `#2E9E5B`, orange `#E08A1E`, red `#D63A3A`, grey `#7A7F87` | |
| appUpdate | `mode` | `Auto` | `Off`, `Notify`, `Auto`. |
| | `channel` | `Stable` | `Stable` or `PreRelease`. |
| | `checkIntervalHours` | `24` | |
| | `applyAutomatically` | `true` | `false` stages the update and waits for **Restart to update**. |
| | `skippedVersion` | `""` | A version you chose to skip. |
| network | `proxy` | `""` | Blank uses the system proxy. Credentials are not stored. |
| | `timeoutSeconds` | `30` | |
| | `userAgent` | `""` | Blank uses a current browser User-Agent. |
| | `maxConcurrent` | `2` | Applies after a restart. |
| advanced | `logLevel` | `Information` | |
| | `enableReleaseHealthFallback` | `true` | Release health as fallback and enrichment. |
| | `enableWebView2Fallback` | `false` | Hidden browser render when the site blocks plain requests. Needs the WebView2 runtime, which is often absent on Servers (skipped cleanly). |

### Tuning the keywords

An empty list means "use the built-in defaults". The Settings window shows the **effective** list; edit it one entry per line. A line starting with `re:` is a regular expression. If you leave a list identical to the defaults it is stored empty, so future default improvements still reach you.

Built-in critical keywords cover authentication and network (RADIUS, 802.1X, EAP, PEAP, MS-CHAPv2, NTLM, Kerberos, NPS, Wi-Fi, WPA, VPN, L2TP, IKEv2, SSTP, Always On VPN), access (Remote Desktop, RDP, Windows Hello, sign-in, logon), security (BitLocker, recovery key, Secure Boot, TPM, Credential Guard, LSASS, Netlogon, certificate), infrastructure (Active Directory, domain controller, DNS, DHCP, SMB, Hyper-V, cluster, print, spooler) and failure modes (boot, BSOD, blue screen, stop error, black screen, crash, data loss, fails to start).

Typical tuning:

- **Too noisy?** Remove a broad word such as `crash` or `print`, or add a regex that is more specific, for example `re:print(er)? (spooler|driver)`.
- **Missing something you care about?** Add it, for example your VPN product name or `re:Citrix`.
- **Change phrases** decide what counts as a behaviour change. Add wording you see Microsoft use, for example `now defaults to`.

After changing keywords, press **Scan now**; severity is recomputed from the cache without refetching.

## App updates

By default PatchRadar checks GitHub Releases once a day and updates itself on the **Stable** channel. It downloads the exe, checks its SHA-256 against the `.sha256` asset in the same release, swaps it in place, and restarts. If the new build does not stay up for 60 seconds on two starts, it rolls back to the previous exe and skips that version. A development build never updates itself.

- **Off**: no automatic checks (the tray's manual check still works, as Notify).
- **Notify**: shows a notification and a menu item that opens the release page.
- **Auto**: downloads, verifies, applies. With `applyAutomatically` off it stages the update and shows **Restart to update**.
- **PreRelease** channel: also offers builds of every push to `main`. These are unsigned and verified by hash only.

### Provenance and integrity

- The `.sha256` file sits in the same release as the exe. It catches corrupt or interrupted downloads. It does **not** stop someone who has compromised the repository or a release token, because they could replace both files.
- Signed releases add an Authenticode signature. Once the updater enforces it, only builds signed through SignPath Foundation are installed on the Stable channel, which does cover a compromised repository.
- Every release asset carries a GitHub build attestation. Verify one by hand:

  ```powershell
  gh attestation verify .\PatchRadar-v1.0.0-win-x64.exe --repo danrhodes/PatchRadar
  ```

- Downloads are limited to `https` on GitHub hosts, and every redirect hop is re-checked.
- SmartScreen reputation for a newly signed app builds over time; early downloads can still show a warning.

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- **Roles.** Author and Approver: `danrhodes`. Only full releases are submitted for signing, and each is approved by the Approver. Pre-releases are not signed.
- **What is signed.** `PatchRadar-<tag>-win-x64.exe` (self-contained) and `PatchRadar-<tag>-win-x64-slim.exe` (framework-dependent). Both are built by the GitHub Actions workflow `.github/workflows/release.yml` from the `main` branch of this repository, on GitHub-hosted runners. The signing configuration is in [`.signpath/policies/patchradar/release-signing.yml`](.signpath/policies/patchradar/release-signing.yml) and [`docs/signpath/artifact-configuration.xml`](docs/signpath/artifact-configuration.xml).
- **Verify a download.** In PowerShell:

  ```powershell
  Get-AuthenticodeSignature .\PatchRadar.exe
  ```

  `Status` must be `Valid` and the signer subject must contain `SignPath Foundation`. Anything else is not an official release. PatchRadar can also check a file itself: `PatchRadar.exe --verify result.txt <file>` writes the verdict to `result.txt`.
- **Privacy statement.** See [Privacy](#privacy).

## Privacy

PatchRadar has no telemetry and sends no data about you or your machine to the author. It contacts only these hosts:

- `support.microsoft.com` and `learn.microsoft.com`, to read the public KB article and release-health pages for updates installed on your machine. The request contains the KB number; the HTTP request itself reveals your IP address to Microsoft, as any web request does.
- `api.github.com`, `github.com`, `objects.githubusercontent.com` and `release-assets.githubusercontent.com`, to check for and download app updates. Turn this off with App updates mode `Off`.

Fetched pages are treated as untrusted: they are parsed to text and never rendered or executed. (The optional WebView2 fallback renders the page in a hidden browser to read its text; navigation is limited to `microsoft.com` and downloads and pop-ups are refused.)

Everything stays on your machine:

| What | Where |
|---|---|
| Settings | `%APPDATA%\PatchRadar\settings.json` |
| Reviewed and notified state | `%APPDATA%\PatchRadar\state.json` |
| KB cache | `%LOCALAPPDATA%\PatchRadar\cache\` |
| Logs (daily, 14 kept) | `%LOCALAPPDATA%\PatchRadar\logs\` |
| Downloaded updates | `%LOCALAPPDATA%\PatchRadar\updates\` |

To uninstall PatchRadar: exit it, delete the exe and the two folders above, and remove the `PatchRadar` value from `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (turning off **Run at login** first does this for you).

PatchRadar **reads** update history only. It never installs, uninstalls or hides updates.

## FAQ and troubleshooting

**An update is grey.** There is no usable data. Hover or open it: the window shows why (Microsoft has no article, the request was blocked, or it has not been fetched yet). Defender definition, Malicious Software Removal Tool and many driver updates have no KB article. Press **Refresh** in the detail window to retry now; otherwise it retries on a back-off (1 hour, 6 hours, then daily; 404s weekly).

**It says "bot challenge" or HTTP 403.** The support site sometimes blocks automated requests. Release health will still supply known issues. For full articles, turn on `enableWebView2Fallback` (needs the WebView2 runtime) or wait; blocked requests retry later. Keep the request volume low: do not shorten the cache intervals.

**Everything is red.** The critical keyword list is deliberately broad. Remove words that do not matter in your environment (see [Tuning the keywords](#tuning-the-keywords)), or review items you have dealt with.

**An update is red but I cannot see a fix.** The At a glance panel says whether a fix or workaround is published. If it says none, there is nothing to apply yet; **Refresh** later, because Microsoft edits these articles.

**The fix is "not installed on this PC".** Install the named update (often an out-of-band update) from Windows Update or the Microsoft Update Catalog, then **Scan now**.

**The icon is missing.** Windows hides new tray icons behind the overflow arrow. Drag it out, or check Settings, Personalisation, Taskbar.

**It will not start twice.** By design: one instance per machine (a named mutex).

**Where is the log?** Tray menu, **Open log folder**. Raise detail with `advanced.logLevel` set to `Debug`.

**Can it uninstall a bad update?** Not today. PatchRadar tells you *which* update and what Microsoft says to do; it does not change the machine.

**Does it work offline?** The list and any cached KB data do. Fetching articles needs the internet.

**Does it support Windows 10?** No.

## Building from source

Requires the .NET 10 SDK on Windows.

```powershell
git clone https://github.com/danrhodes/PatchRadar.git
cd PatchRadar
dotnet build src/PatchRadar -c Release
dotnet test tests/PatchRadar.Tests -c Release       # offline; no test touches the network
```

Self-contained single file, as the release workflow builds it:

```powershell
dotnet publish src/PatchRadar -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish
```

Useful command-line modes:

| Command | Purpose |
|---|---|
| `PatchRadar.exe --diag out.txt [KB5127648 …] [--webview]` | Headless: times update collection, fetches the KBs live, reports release health and (optionally) WebView2. |
| `PatchRadar.exe --verify out.txt <file>` | Authenticode verdict for a file. |
| `tools/test-matrix.ps1 -Exe PatchRadar.exe` | Self-check for a machine in the OS matrix. |
| `tools/make-icons.ps1` | Regenerates the icons in `assets/`. |

The architecture and every decision are in [guide.md](guide.md); progress and the decision log are in [tracker.md](tracker.md). Releases are built by GitHub Actions: every push to `main` makes an unsigned pre-release, and a manual run makes a signed full release.

## Contributing

Issues and pull requests are welcome. Please:

- Build parsers against **saved fixtures** (`tests/PatchRadar.Tests/Fixtures`), not guesses, and locate content by heading text rather than CSS class.
- Keep tests offline.
- Include a fixture when you report a KB page that parses wrongly (save the article's HTML).

## Licence

MIT. See [LICENSE](LICENSE).
