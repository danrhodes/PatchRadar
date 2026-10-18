# PatchRadar

Tray tool that lists recently installed Windows updates and flags risky ones from their Microsoft KB articles. See [guide.md](guide.md) for the full specification.

> Work in progress. A full README (screenshots, settings reference, FAQ) is planned for P4.

## Install location

Install per-user, for example `%LOCALAPPDATA%\Programs\PatchRadar\PatchRadar.exe`. The folder is writable without elevation, so the built-in updater can replace the exe in place. In `Program Files` the updater cannot write, so it falls back to notify-only and tells you when a new version exists.

If your antivirus or EDR (Defender, ThreatLocker and similar) objects to an exe that renames and replaces itself, allowlist `PatchRadar.exe` in its install folder. The updater renames the running exe to `PatchRadar.exe.old`, copies the new one into place and restarts.

## App updates and their limits

By default PatchRadar checks GitHub Releases once a day (Settings, App updates) and updates itself on the Stable channel. It downloads the new exe, checks its SHA-256 against the `.sha256` file in the same release, swaps it in and restarts. If the new build fails to stay up for 60 seconds on two starts, it rolls back to the previous exe and skips that version.

What that does and does not protect against:

- The `.sha256` file sits in the same release as the exe. It catches corrupt or interrupted downloads. It does **not** stop someone who has compromised the repository or a release token, because they could replace both files.
- Stable releases are meant to be Authenticode-signed (see below). Once the updater enforces signatures, only builds signed through SignPath Foundation are installed, which does cover a compromised repository. Enforcement is switched on after the first signed release has been verified on a clean machine.
- Every release asset also carries a GitHub build attestation. Verify one by hand with:
  `gh attestation verify .\PatchRadar-<tag>-win-x64.exe --repo danrhodes/PatchRadar`
- Pre-releases (builds of every push to `main`) are unsigned. The Stable channel ignores them. SmartScreen reputation for a newly signed app builds over time; the first downloads can still show a warning.

Set Settings, App updates, Mode to `Notify` to be told about updates without installing them, or `Off` to disable checks.

## Code signing policy

Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- **Roles.** Author and Approver: `danrhodes`. Only full releases are submitted for signing, and each is approved by the Approver. Pre-releases are not signed.
- **What is signed.** `PatchRadar-<tag>-win-x64.exe` (self-contained) and `PatchRadar-<tag>-win-x64-slim.exe` (framework-dependent). Both are built by the GitHub Actions workflow `.github/workflows/release.yml` from the `main` branch of this repository, on GitHub-hosted runners. The signing configuration is in [`.signpath/policies/patchradar/release-signing.yml`](.signpath/policies/patchradar/release-signing.yml) and [`docs/signpath/artifact-configuration.xml`](docs/signpath/artifact-configuration.xml).
- **Verify a download.** In PowerShell:

  ```powershell
  Get-AuthenticodeSignature .\PatchRadar.exe
  ```

  `Status` must be `Valid` and the signer subject must contain `SignPath Foundation`. Anything else is not an official release.
- **Privacy statement.** PatchRadar has no telemetry and sends no data about you or your machine to the author. It contacts only these hosts:
  - `support.microsoft.com` and `learn.microsoft.com`, to read the public KB article and release-health pages for updates installed on your machine (the request contains the KB number; the HTTP request itself reveals your IP address to Microsoft, as any web request does).
  - `api.github.com`, `github.com`, `objects.githubusercontent.com` and `release-assets.githubusercontent.com`, to check for and download app updates (turn this off with App updates mode `Off`).

  Settings, review state and cached KB text stay on your machine under `%APPDATA%\PatchRadar` and `%LOCALAPPDATA%\PatchRadar`.

## Licence

MIT. See [LICENSE](LICENSE).
