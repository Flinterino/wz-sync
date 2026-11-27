# Wars & Zombies Sync

Wars & Zombies Sync is a Windows utility that checks and updates the managed files in an existing CurseForge profile. The WPF app follows the approved English dark-olive design. [UI mockups](https://github.com/Flinterino/wz-sync/pull/1)

Choose your existing profile and select Lite or Full. Check for updates, then review every file, its current and proposed sizes, and its source. Open **Show SHA-256 details** to see both hashes. **Apply & Play** approves the displayed changes. Close Minecraft before applying. Once the app has verified the managed files, open your profile in CurseForge and press **Play**.

The app looks for CurseForge’s default Windows Instances folder. Use **Browse** for a custom location. Required manual files block Apply. Update those files in CurseForge and check again. [CurseForge folder setup](https://support.curseforge.com/support/solutions/articles/9000218572)

**Restore backup** shows the full inverse plan and asks you to approve it. Check for updates again after a restore. **View sync log** shows the latest operation log, and **Open log folder** opens the existing local `.wzsync` folder.

The app reads the manifest and its checksum from the same tagged GitHub release. The manifest version matches the release tag. Checks display a missing-release error until both assets are available for the selected pack. [GitHub release asset links](https://docs.github.com/en/repositories/releasing-projects-on-github/linking-to-releases)

## Startup checks and pack settings

When WzSync opens, it compares the selected profile with the latest published manifest and reports whether an update is available. It remembers the last selection on this PC. Use **Check for updates** to review files and approve an update.

**Review pack default settings** offers the bundled mod configs plus first-run options and keybind presets used by Default Options. Existing personal options are preserved. Changed mod configs can be restored with **Restore backup**.

**Set up Standalone Lite** creates an isolated profile from a matching local Lite installation. The official Minecraft Launcher handles sign-in and Play after NeoForge 21.1.265 has been installed. The preview requires a local Lite source for the initial import. Standalone Lite caps Minecraft's Java heap at 10 GB and chooses a lower limit on PCs with less RAM.

## Download and verify

Get `WzSync.App.exe` and `WzSync.App.exe.sha256` from the same official GitHub release. Download the Windows x64 executable once and run it as your usual Windows user. [Wars & Zombies Sync releases](https://github.com/Flinterino/wz-sync/releases)

Before opening the executable, open PowerShell in the download folder and compare the checksum. Both lines should show the same 64 hexadecimal characters. If they differ, discard the download.

```powershell
(Get-FileHash -LiteralPath ./WzSync.App.exe -Algorithm SHA256).Hash.ToLowerInvariant()
(Get-Content -LiteralPath ./WzSync.App.exe.sha256 -Raw).Trim().Split(' ')[0]
```

The executable is unsigned. Windows SmartScreen may warn that it does not recognize the app. A matching SHA-256 confirms the bytes against the checksum from the release. It does not prove the publisher’s identity. Use the official release as your source and leave Windows security protections enabled. [Microsoft SmartScreen guidance](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation)

The app includes its .NET runtime in the executable. On startup .NET may extract bundled native runtime files under `%TEMP%\.net`, or under a location configured through `DOTNET_BUNDLE_EXTRACT_BASE_DIR`. Sync data, backups and logs stay in the selected CurseForge profile. [How .NET single-file extraction works](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/#native-libraries)

The app needs no admin rights or installer. It runs no downloaded mods or other payloads and makes no registry or startup changes. It installs no services and has no telemetry or automatic launcher updates.

## Build and test

Install the .NET 10 SDK specified in `global.json`. On Windows, run the WPF app or the full Release test suite.

```sh
dotnet run --project src/WzSync.App
dotnet test --configuration Release
```

GitHub Actions runs Core tests on Linux and Windows and the WPF tests on Windows. On Linux, run `dotnet test tests/WzSync.Core.Tests --configuration Release`. The tests use temporary profiles and simulated release responses.

The release workflow builds a self-contained Windows x64 executable and verifies that two fresh builds of the tagged commit match. [Release operator guide](docs/release.md)

See the [Core caller contract](docs/core-api.md) for planning, explicit approval, restore and recovery. Verification evidence and platform limits are in the [L2 review report](docs/reviews/2026-11-18-core.md).

## File and network policy

The client contains a fixed allow-list of top-level mod files and individual pack configs. A downloaded manifest can only narrow it. Personal options, worlds, shaders, resource packs, maps and scripts are outside synchronization scope. The caller chooses the profile and explicitly approves a complete plan before applying it. Internal staging, journals, backups and local logs live in the selected profile's `.wzsync` directory.

Paths are revalidated case-insensitively. Traversal, absolute paths, ambiguous Windows filenames, symlinks and junctions are refused, including in internal state paths. Run with the game closed. Do not change the profile while an operation is in progress. Path revalidation is defense against unsafe filesystem entries; it is not an operating-system sandbox against a hostile process racing filesystem changes under the same account.

Downloads use HTTPS and each redirect hop must stay on an approved host for the declared provider. Only GitHub release assets, the approved CurseForge CDNs and Modrinth's CDN are allowed. Every file must match both its manifest byte size and SHA256. All new payloads are staged and verified before managed files change. Missing manual-download files block the entire apply; a mod-page link is not a download permission bypass.

**A `.sha256` sidecar verifies integrity, not publisher identity.** A checksum fetched with an untrusted manifest does not authenticate that manifest or its publisher. This Core stage does not establish a signed release-trust policy. Do not treat arbitrary manifests as owner-approved simply because their hashes match.

The app requires standard user access. CurseForge handles game launch. The only network requests made by the application are for release metadata files and approved payload downloads. Opening an official mod page is a separate browser action.

## Backup and restore

An approved sync keeps old replaced/removed bytes in a local backup and records a recovery journal. Restore requires review of the inverse file plan and verifies both backup bytes and current profile state. Modified current files must be reviewed instead of silently overwritten. Restoring an old version is not evidence that the profile matches the current server pack.

Keep `.wzsync` intact while an operation or recovery is pending. Backups are local, not a replacement for a separate copy of valuable data. No automatic backup deletion is included.

## Verification scope

WPF tests cover the setup, change review, cancel, apply, and restore paths with a temporary profile. The Play handoff uses written CurseForge instructions. Release packaging, signing, and a game join with a real profile require separate verification.

MIT licensed.
