# Core caller contract

Target: .NET 10. Namespace: `WzSync.Core`. This library implements file synchronization, not the WPF application or manifest publisher authentication.

## Review before apply

```csharp
var profile = new ProfilePaths(selectedProfileDirectory);
var manifest = ManifestParser.Parse(manifestJson);
var plan = SyncPlanner.Create(profile, manifest);

// Display every entry: Path, Action, Current and Desired size/SHA256/source.
// Planning reads managed candidates only and does not create state or change files.
// CanApply=false means missing/mismatched manual files must be resolved and rechecked.
using var downloader = new VerifiedDownloader();
var engine = new TransactionEngine(profile, downloader);
if (userApprovedDisplayedPlan && plan.CanApply)
    await engine.ApplyAsync(plan, cancellationToken);
```

`SyncPlan`, manifest rows and entries are immutable; callers cannot construct arbitrary plans through the public API. A plan belongs to one selected profile. Apply rechecks the complete managed file set and snapshots, including KEEP/manual files, after staging. A changed plan must be displayed again; approval of an older plan is not approval of new changes.

The default downloader owns its HTTP handler with automatic redirects disabled. Each initial URL and every redirect hop passes the fixed source policy. `StageAsync` accepts only a new path under the selected profile's `.wzsync/tmp/`; it never applies a managed file. The engine independently rechecks staged size and hash before applying. Failed staging may leave an operation-owned partial file in internal state.

## Review before restore

```csharp
var restore = engine.PrepareRestoreLast();
// Display all restore.Entries: Path, Action, Current and Desired snapshots.
if (userApprovedDisplayedRestore)
    await engine.RestoreAsync(restore, cancellationToken);
```

Restore previews inverse additions, replacements and removals, with KEEP entries still available for full state review. It refuses changed current files or missing/corrupt original backup bytes. Current versions are backed up before restoring prior bytes. A successful restore consumes the last-sync restore receipt and preserves both backup sets; it does not claim a known old pack version or server readiness. A no-change apply preserves the existing restore point.

## Failure and interrupted operation

Only one engine mutation may hold the profile's exclusive state lock. An active recovery journal prevents a new apply from silently starting over. A missing index with existing backup state stops apply and recovery, preserving the remaining data. `engine.Recover()` attempts a guarded rollback of an interrupted operation and returns true when it recovered one. If it cannot prove which bytes are safe, it preserves unknown current data and reports failure; callers must not show Ready. Replan after recovery or restore.

All required payloads verify before managed mutation. Once mutation starts, the engine must finish or recover its journaled operation; cancellation cannot be implemented by abruptly killing or deleting the state directory. Local logs contain phases and validated relative paths, without raw filesystem exceptions or signed URLs. Retain journals and backups until recovery has completed. Recovery tests model a process interruption with filesystem state intact. Recovery after power loss depends on which filesystem updates reach storage.

No UI approval is inferred from calling the planner. Apply/Restore are the explicit caller approval boundaries. Do not automatically retry them using an undisplayed new plan. Use the game closed and keep the profile unchanged while applying; the managed resolver does not provide an OS sandbox against hostile concurrent directory renames.
