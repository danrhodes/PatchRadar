# Bill of materials

Every third-party component that ships in, or builds, PatchRadar. Use it to check new CVEs against what PatchRadar actually contains.

- **Last reviewed:** 2026-11-03, against PatchRadar source on `main`
- **Source of truth:** [src/PatchRadar/PatchRadar.csproj](src/PatchRadar/PatchRadar.csproj) and [.github/workflows/release.yml](.github/workflows/release.yml). If this file and those disagree, they win; fix this file.
- **Regenerate the package lists:**

  ```powershell
  dotnet list src/PatchRadar package --include-transitive
  dotnet list src/PatchRadar package --vulnerable --include-transitive
  dotnet list src/PatchRadar package --deprecated
  ```

  The `--vulnerable` check reads the NuGet advisory database (GitHub Advisory Database), so it is the quickest CVE check for the NuGet packages below.

## 1. Runtime packages (NuGet)

These are bundled into the release exe.

| Component | Version | Type | Licence | Used for | Reachable from untrusted input? |
|---|---|---|---|---|---|
| [HtmlAgilityPack](https://www.nuget.org/packages/HtmlAgilityPack) | 1.13.1 | Direct | MIT | Parsing Microsoft KB article HTML | **Yes.** Parses pages fetched from `support.microsoft.com` and `learn.microsoft.com`. Highest-priority package for CVE review. |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | 1.0.4258.31 | Direct | Microsoft Software License Terms (see package) | Hidden browser render fallback when the support site blocks plain requests | Yes, as a loader. Navigation is limited to `microsoft.com`. The browser engine itself is the separate WebView2 Runtime (section 3). |
| [H.NotifyIcon.Wpf](https://www.nuget.org/packages/H.NotifyIcon.Wpf) | 2.4.1 | Direct | MIT | Tray icon and context menu | No. |
| [H.NotifyIcon](https://www.nuget.org/packages/H.NotifyIcon) | 2.4.1 | Transitive (via H.NotifyIcon.Wpf) | MIT | Tray icon core | No. |
| [H.GeneratedIcons.System.Drawing](https://www.nuget.org/packages/H.GeneratedIcons.System.Drawing) | 2.4.1 | Transitive (via H.NotifyIcon) | MIT | Generated icon resources | No. |
| [System.Management](https://www.nuget.org/packages/System.Management) | 10.0.13 | Direct | MIT | WMI queries (OS, uptime, restart state, servicing records) | No. Queries the local machine only. |

## 2. .NET runtime

| Component | Version | Notes |
|---|---|---|
| .NET 10 (`net10.0-windows`), WPF | 10.0.x, floating | **Self-contained build** (`PatchRadar-<version>-win-x64.exe`) embeds the runtime that was current on the GitHub runner on build day (`dotnet-version: 10.0.x`). A runtime CVE means a rebuild and a new release. **Slim build** (`-slim.exe`) uses the .NET 10 Desktop Runtime installed on the machine, so the user patches it through Windows Update. |

To find which runtime a given release embeds, run the exe's `--diag` mode or check the file's version resource. Record it in the release notes when cutting a release.

## 3. Components PatchRadar uses but does not ship

| Component | Where it comes from | Notes |
|---|---|---|
| Microsoft Edge WebView2 Runtime | Installed on the machine (Windows 11 includes it; often absent on Servers) | Optional. If missing, the fallback is skipped cleanly. Patched by Microsoft, not by PatchRadar. |
| Windows Update Agent (COM), WMI, DISM, `wusa.exe` | Windows | Called for update history, pending updates and the optional admin uninstall. |
| Windows Authenticode / `WinVerifyTrust` | Windows | Used to check signatures. |

## 4. Build and release pipeline

| Component | Version | Used for |
|---|---|---|
| .NET SDK | 10.0.x (via `actions/setup-dotnet`) | Build, test, publish |
| `windows-latest` GitHub-hosted runner | Rolling | Build environment |
| [actions/checkout](https://github.com/actions/checkout) | v4 | Source checkout |
| [actions/setup-dotnet](https://github.com/actions/setup-dotnet) | v4 | SDK install |
| [actions/attest-build-provenance](https://github.com/actions/attest-build-provenance) | v3 | Build attestation on release assets |
| [ncipollo/release-action](https://github.com/ncipollo/release-action) | v1 | Publishing GitHub Releases |
| [actions/github-script](https://github.com/actions/github-script) | v7 | Release housekeeping |
| [SignPath Foundation](https://signpath.org/) | Service | Authenticode signing of full releases (policy in [.signpath/](.signpath/policies/patchradar/release-signing.yml)) |

The actions are pinned to major-version tags, not commit SHAs. Pinning to SHAs is a possible hardening step.

## 5. Test-only packages (not shipped)

From [tests/PatchRadar.Tests/PatchRadar.Tests.csproj](tests/PatchRadar.Tests/PatchRadar.Tests.csproj). These never reach the release exe.

| Component | Version | Licence |
|---|---|---|
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| coverlet.collector | 6.0.4 | MIT |

## 6. Network endpoints

Not software components, but the services PatchRadar trusts. A compromise of any of them matters as much as a library CVE.

| Host | Purpose |
|---|---|
| `support.microsoft.com`, `learn.microsoft.com` | KB articles and Windows release health (parsed as untrusted) |
| `api.github.com`, `github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com` | App update checks and downloads |

## Checking for CVEs

1. Run the `dotnet list ... --vulnerable --include-transitive` command above. Anything listed needs a decision.
2. For a runtime or WebView2 advisory, check whether the self-contained build predates the fix, and whether the slim build's host machines are patched.
3. Prioritise by the **Reachable from untrusted input?** column. HtmlAgilityPack and WebView2 handle content from the internet; the rest do not.
4. Record the outcome below.

### Review log

| Date | Reviewer | Result |
|---|---|---|
| 2026-11-03 | Initial list created | `dotnet list package --vulnerable --include-transitive`: no vulnerable packages found. Embedded .NET runtime version not yet recorded. |
