# Wars & Zombies Sync

An update utility under development for an existing CurseForge Wars & Zombies profile. This repository currently contains the .NET 10 Core library; a downloadable Windows app and CurseForge integration belong to later stages. The owner approved English UI mockups in dark / olive in [PR1](https://github.com/Flinterino/wz-sync/pull/1).

## Build and test

Install the .NET 10 SDK specified in `global.json`, then run:

```sh
dotnet test --configuration Release
```

Production code uses the .NET base class library. Test dependencies are isolated to the test project. GitHub Actions runs the tests on Linux and Windows; no Minecraft installation, credentials or live server is required.

See the [Core caller contract](docs/core-api.md) for planning, explicit approval, restore and recovery. Verification evidence and platform limits are in the [L2 review report](docs/reviews/2026-11-18-core.md).

## File and network policy

The client contains a fixed allow-list of top-level mod files and individual pack configs. A downloaded manifest can only narrow it. Personal options, worlds, shaders, resource packs, maps and scripts are outside synchronization scope. The caller chooses the profile and explicitly approves a complete plan before applying it. Internal staging, journals, backups and local logs live in the selected profile's `.wzsync` directory.

Paths are revalidated case-insensitively. Traversal, absolute paths, ambiguous Windows filenames, symlinks and junctions are refused, including in internal state paths. Run with the game closed. Do not change the profile while an operation is in progress. Path revalidation is defense against unsafe filesystem entries; it is not an operating-system sandbox against a hostile process racing filesystem changes under the same account.

Downloads use HTTPS and each redirect hop must stay on an approved host for the declared provider. Only GitHub release assets, the approved CurseForge CDNs and Modrinth's CDN are allowed. Every file must match both its manifest byte size and SHA256. All new payloads are staged and verified before managed files change. Missing manual-download files block the entire apply; a mod-page link is not a download permission bypass.

**A `.sha256` sidecar verifies integrity, not publisher identity.** A checksum fetched with an untrusted manifest does not authenticate that manifest or its publisher. This Core stage does not establish a signed release-trust policy. Do not treat arbitrary manifests as owner-approved simply because their hashes match.

The library never executes downloaded code or installs services, startup entries or telemetry. It needs no administrator rights. It does not launch Minecraft or contact the game server.

## Backup and restore

An approved sync keeps old replaced/removed bytes in a local backup and records a recovery journal. Restore requires review of the inverse file plan and verifies both backup bytes and current profile state. Modified current files must be reviewed instead of silently overwritten. Restoring an old version is not evidence that the profile matches the current server pack.

Keep `.wzsync` intact while an operation or recovery is pending. Backups are local, not a replacement for a separate copy of valuable data. No automatic backup deletion is included.

## Stage limits

This is the Core stage. WPF screens, instance discovery, application releases/signing, CurseForge handoff and an owner-supervised game join are separate work. Unit tests exercise temporary profiles; they do not certify an actual player profile or modpack distribution readiness.

MIT licensed.
