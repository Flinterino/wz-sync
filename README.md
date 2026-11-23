# Wars & Zombies Sync

Wars & Zombies Sync is a Windows utility that checks and updates the managed files in an existing CurseForge profile. The WPF app follows the approved English dark-olive design. [UI mockups](https://github.com/Flinterino/wz-sync/pull/1)

Choose your existing profile and select Lite or Full. Check for updates, then review every file, its current and proposed sizes, and its source. Open **Show SHA-256 details** to see both hashes. **Apply & Play** approves the displayed changes. Close Minecraft before applying. Once the app has verified the managed files, open your profile in CurseForge and press **Play**.

The app looks for CurseForge’s default Windows Instances folder. Use **Browse** for a custom location. Required manual files block Apply. Update those files in CurseForge and check again. [CurseForge folder setup](https://support.curseforge.com/support/solutions/articles/9000218572)

**Restore backup** shows the full inverse plan and asks you to approve it. Check for updates again after a restore. **View sync log** shows the latest operation log, and **Open log folder** opens the existing local `.wzsync` folder.

The app reads the manifest and its checksum from the same tagged GitHub release. The manifest version matches the release tag. Checks display a missing-release error until both assets are available for the selected pack. [GitHub release asset links](https://docs.github.com/en/repositories/releasing-projects-on-github/linking-to-releases)

## Build and test

Install the .NET 10 SDK specified in `global.json`. On Windows, run the WPF app or the full Release test suite.

```sh
dotnet run --project src/WzSync.App
dotnet test --configuration Release
```

GitHub Actions runs Core tests on Linux and Windows and the WPF tests on Windows. On Linux, run `dotnet test tests/WzSync.Core.Tests --configuration Release`. The tests use temporary profiles and simulated release responses.

For a local self-contained Windows x64 build:

```sh
dotnet publish src/WzSync.App --configuration Release --runtime win-x64 --self-contained true
```

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
