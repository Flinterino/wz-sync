using System.IO;
using System.Text.Json;
using WzSync.Core;

namespace WzSync.App;

public partial class MainWindow
{
    private CancellationTokenSource? automaticCheck;
    private bool checkOnSelection;
    private bool persistSelection;
    private sealed record SavedSelection(string Path, PackChoice Pack);

    private void StartAutomaticChecks(bool remember)
    {
        if (checkOnSelection) return;
        persistSelection = remember;

        if (remember && ReadSelection() is { } saved)
        {
            var preferred = profiles.FirstOrDefault(item => string.Equals(item.Path, saved.Path, StringComparison.OrdinalIgnoreCase));
            if (preferred == null)
            {
                try
                {
                    var root = ProfileSelection.Open(saved.Path).Root;
                    preferred = new ProfileCandidate(Path.GetFileName(root), root);
                    profiles.Add(preferred);
                }
                catch (Exception ex) when (IsExpected(ex)) { }
            }
            if (preferred != null)
            {
                FullPack.IsChecked = !preferred.IsStandalone && saved.Pack == PackChoice.Full;
                LitePack.IsChecked = FullPack.IsChecked != true;
                ProfileList.SelectedItem = preferred;
            }
        }

        if (ProfileList.SelectedItem == null)
            ProfileList.SelectedItem = profiles.FirstOrDefault(item => item.Name.Equals("Wars & Zombies Lite", StringComparison.OrdinalIgnoreCase))
                ?? profiles.FirstOrDefault(item => item.IsStandalone);

        checkOnSelection = true;
        StartProfileCheck();
    }

    private void StartProfileCheck()
    {
        CancelProfileCheck();
        if (!checkOnSelection) return;
        if (session.Profile is not { } profile)
        {
            UpdateNotice.Text = "Select a profile to check for updates.";
            return;
        }

        var pack = session.Pack;
        if (persistSelection) SaveSelection(new SavedSelection(profile.Root, pack));
        var check = new CancellationTokenSource();
        automaticCheck = check;
        UpdateNotice.Text = "Checking for updates...";
        _ = CheckProfileAsync(profile, pack, check);
    }

    private async Task CheckProfileAsync(ProfilePaths profile, PackChoice pack, CancellationTokenSource check)
    {
        try
        {
            var plan = await session.CheckAvailabilityAsync(profile, pack, check.Token);
            if (automaticCheck == check) ReportAvailability(plan);
        }
        catch (OperationCanceledException) when (check.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (automaticCheck == check)
                UpdateNotice.Text = ex is RecoveryRequiredException
                    ? "Recovery needed. Select Check for updates to review."
                    : "Could not check for updates. Select Check for updates to retry.";
        }
        finally
        {
            if (automaticCheck == check) automaticCheck = null;
            check.Dispose();
        }
    }

    private void ReportAvailability(SyncPlan plan)
    {
        var changed = plan.Entries.Count(row => row.Action != PlanAction.Keep);
        UpdateNotice.Text = changed == 0 ? $"Up to date ({plan.Manifest.Version})."
            : $"Update available: {ChangeCount(changed)} in {plan.Manifest.Version}. Select Check for updates to review.";
    }

    private void CancelProfileCheck()
    {
        var previous = automaticCheck;
        automaticCheck = null;
        previous?.Cancel();
    }

    private static string PreferencePath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "WarsAndZombiesSync", "selection.json");

    private static SavedSelection? ReadSelection()
    {
        try
        {
            var file = new FileInfo(PreferencePath);
            if (!file.Exists || file.Length > 4096 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
            var selection = JsonSerializer.Deserialize<SavedSelection>(File.ReadAllBytes(file.FullName));
            return selection != null && !string.IsNullOrEmpty(selection.Path) &&
                Path.IsPathFullyQualified(selection.Path) && Enum.IsDefined(selection.Pack) ? selection : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void SaveSelection(SavedSelection selection)
    {
        var directory = Path.GetDirectoryName(PreferencePath)!;
        try
        {
            Directory.CreateDirectory(directory);
            new ProfilePaths(directory).CheckRoot();
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(selection));
                File.Move(temporary, PreferencePath, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }
}
