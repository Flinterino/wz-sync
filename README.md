# Wars & Zombies Sync

A planned, transparent Windows utility for keeping an existing CurseForge profile in sync with the Wars & Zombies pack.

**Current stage: UI approved; Core implementation next.** There is no downloadable launcher or sync engine yet. The files here are review-only mockups; they do not read or change a Minecraft profile.

- [L3 interactive mockup and review guide](design/l3/README.md)
- [Six-screen overview](design/l3/previews/overview.png)

Future implementation: a small client that previews changes, verifies files, keeps a local backup/log and hands game launch back to CurseForge. Only approved mod files and specific pack configs are in scope. The client will not manage worlds, personal options, shader packs or server-delivered resource packs.

MIT licensed. Builds and releases will be handled through GitHub Actions in a later stage.
