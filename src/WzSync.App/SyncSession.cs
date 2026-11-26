using System.IO;
using WzSync.Core;

namespace WzSync.App;

internal sealed class RecoveryRequiredException() : InvalidOperationException("An interrupted sync needs recovery.");

// All session state belongs to the WPF dispatcher. Background work receives a captured profile and pack.
internal sealed class SyncSession(Func<PackChoice, CancellationToken, Task<SyncManifest>> fetch, VerifiedDownloader downloader)
{
    internal ProfilePaths? Profile { get; private set; }
    internal PackChoice Pack { get; private set; } = PackChoice.Lite;
    internal SyncPlan? SyncReview { get; private set; }
    internal RestorePlan? RestoreReview { get; private set; }
    internal bool IsBusy { get; private set; }
    private SyncPlan? appliedReview;

    internal void Select(string? directory, PackChoice pack)
    {
        DiscardReview();
        Profile = null;
        Pack = pack;
        if (directory != null) Profile = ProfileSelection.Open(directory);
    }

    internal void DiscardReview()
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the current operation to finish.");
        SyncReview = null;
        RestoreReview = null;
        appliedReview = null;
    }

    internal Task<SyncPlan> CheckAsync(CancellationToken token)
    {
        var pack = Pack;
        return RunAsync(async (profile, engine) =>
        {
            RequireNoRecovery(engine);
            var manifest = await fetch(pack, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            RequireNoRecovery(engine);
            var plan = SyncPlanner.Create(profile, manifest);
            token.ThrowIfCancellationRequested();
            return plan;
        }, plan => SyncReview = plan);
    }

    internal async Task VerifyCurrentAsync(SyncPlan shown, CancellationToken token)
    {
        if (!ReferenceEquals(shown, appliedReview) &&
            (!ReferenceEquals(shown, SyncReview) || shown.Entries.Any(row => row.Action != PlanAction.Keep)))
        {
            DiscardReview();
            throw new InvalidOperationException("Check the selected profile before playing.");
        }
        await RunAsync((profile, engine) =>
        {
            RequireNoRecovery(engine);
            token.ThrowIfCancellationRequested();
            if (SyncPlanner.Create(profile, shown.Manifest).Entries.Any(row => row.Action != PlanAction.Keep))
                throw new InvalidDataException("Profile files changed since the last check.");
            RequireNoRecovery(engine);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        });
    }

    internal async Task ApplyAsync(SyncPlan shown, CancellationToken token, IProgress<SyncProgress>? progress = null)
    {
        if (!ReferenceEquals(shown, SyncReview) || !shown.CanApply ||
            shown.Entries.All(row => row.Action == PlanAction.Keep))
        {
            DiscardReview();
            throw new InvalidOperationException("Review an applicable plan before approving the sync.");
        }
        await RunAsync(async (_, engine) =>
        {
            await engine.ApplyAsync(shown, token, progress).ConfigureAwait(false);
            return shown;
        }, plan => appliedReview = plan);
    }

    internal Task<RestorePlan> PrepareRestoreAsync() => RunAsync((_, engine) =>
    {
        RequireNoRecovery(engine);
        return Task.FromResult(engine.PrepareRestoreLast());
    }, plan => RestoreReview = plan);

    internal async Task RestoreAsync(RestorePlan shown)
    {
        if (!ReferenceEquals(shown, RestoreReview))
        {
            DiscardReview();
            throw new InvalidOperationException("Review the restore plan before approving the restore.");
        }
        await RunAsync(async (_, engine) =>
        {
            await engine.RestoreAsync(shown).ConfigureAwait(false);
            return true;
        });
    }

    internal Task<bool> RecoverAsync() => RunAsync((_, engine) => Task.FromResult(engine.Recover()));

    private static void RequireNoRecovery(TransactionEngine engine)
    {
        if (engine.HasInterruptedOperation()) throw new RecoveryRequiredException();
    }

    private async Task<T> RunAsync<T>(Func<ProfilePaths, TransactionEngine, Task<T>> work, Action<T>? publish = null)
    {
        DiscardReview();
        var profile = Profile ?? throw new InvalidOperationException("Choose a profile first.");
        IsBusy = true;
        try
        {
            var result = await Task.Run(() => work(profile, new TransactionEngine(profile, downloader)));
            publish?.Invoke(result);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
