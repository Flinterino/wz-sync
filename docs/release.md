# Prepare a launcher release

The tag workflow packages the Windows x64 app and appends its executable and checksum to an existing draft in Flinterino/wz-sync. The owner or authorized release operator stages the approved inputs. Claude reviews the bundle, source and run before release publication is coordinated.

## Stage the approved draft input

1. Use the final Full and Lite outputs from the approved pack builder run with `--manifest`. Keep `manifest-full.json`, `manifest-lite.json`, their `.sha256` files, and the flat contents of `sync-assets/<version>/` together in one directory. Verify distribution permission for every owned asset before staging. The owned-asset validator accepts managed configs and approved `mods/wz_*.jar` rows. Third-party files served by CurseForge or Modrinth are represented by manifest URLs only.
2. Validate the flat bundle with the pinned .NET 10 SDK. The planned tag must exactly equal the version inside both manifests.

   ```powershell
   dotnet run --project tools/WzSync.Release --configuration Release -- 'PATH_TO_APPROVED_BUNDLE' 'TAG_FROM_MANIFEST'
   ```

3. In the public launcher repository, stage a draft for that exact version with both manifest/checksum pairs and only the validated owned files. Keep any test release as a draft marked pre-release. The tag workflow never publishes drafts.
4. Push a new tag pointing at the reviewed launcher commit. Leave the draft unpublished and its assets untouched until the tag run completes. GitHub's upload endpoint accepts a release ID and has no atomic draft-state condition. The job checks the exact draft immediately before each append. The workflow verifies the source commit, validates the already-staged draft and appends only `WzSync.App.exe` and `WzSync.App.exe.sha256`. If either already matches the build, it is preserved. A conflicting file stops the workflow.
5. Review the two new files and the complete draft asset list against the successful Actions run. Production publication and the first real-profile game check are separate from build verification.

## Build verification

The Windows job runs the tests, publishes twice from separate fresh snapshots of the same commit, and compares executable SHA-256 values and byte lengths. It publishes a single Windows x64 file with the .NET runtime included. The results establish reproducibility under the pinned SDK and runner environment used for the run.

The read-only build job passes the launcher to a separate draft job with scoped repository content access. That job verifies both the executable and its checksum against the build output. It verifies the tagged source and both pack manifests, and rechecks every referenced owned asset without executing it.

To run the same packaging smoke check locally on Windows, use a new output directory. The script builds the checked-out Git commit. [Microsoft single-file deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/)

```powershell
./tools/release/Build-Launcher.ps1 -OutputDirectory ./scratch/release-smoke
```

Offline tests use synthetic inputs. Keep fixture manifests out of release drafts and the player feed. An approved pack bundle is required for a tagged release test.
