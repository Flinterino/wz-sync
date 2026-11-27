using System.Windows;
using WzSync.Core;

namespace WzSync.App;

public partial class MainWindow
{
    private async void PackDefaults_Click(object sender, RoutedEventArgs args)
    {
        var profile = session.Profile;
        if (profile == null || operation != null) return;
        await RunOperationAsync("Checking pack default settings", "Comparing bundled settings with the selected profile.",
            true, async token =>
            {
                var defaults = new PackDefaults();
                var pack = session.Pack;
                var plan = await Task.Run(() => SyncPlanner.Create(profile, defaults.Manifest(pack)), token);
                token.ThrowIfCancellationRequested();
                SwitchPane(ReviewPane);
                ReviewHeading.Text = "Review pack default settings";
                ReviewSubtitle.Text = "Apply these mod settings and install the first-run options and keybind defaults.";
                ReviewSummary.Text = $"{ProfileName}  -  {ChangeCount(plan.Entries.Count(row => row.Action != PlanAction.Keep))}";
                ReviewGrid.ItemsSource = plan.Entries.Select(ReviewRow.FromPlan)
                    .Select(row => row with { Source = "Bundled pack defaults" }).ToArray();
                ShowHashes.IsChecked = false;
                SetManualNotice("Your existing options.txt and keybind settings stay in place. Default Options uses the preset for a new profile.");
                SetActions("Apply pack defaults", plan.Entries.Any(row => row.Action != PlanAction.Keep)
                    ? () => ApplyDefaultsAsync(defaults, profile, plan) : null, "Back", ShowSetup,
                    "Close Minecraft before applying. You can restore the previous config files.");
            });
    }

    private Task ApplyDefaultsAsync(PackDefaults defaults, ProfilePaths profile, SyncPlan plan) =>
        RunOperationAsync("Applying pack default settings", "Verifying the bundled files and backing up the selected profile.",
            true, async token =>
            {
                using var gameCheck = System.Diagnostics.Process.GetProcessesByName("javaw").FirstOrDefault();
                if (gameCheck != null)
                    throw new System.IO.InvalidDataException("Close Minecraft before applying pack defaults.");
                var progress = new Progress<SyncProgress>(report =>
                {
                    if (operation?.Token == token) ShowSyncProgress(report);
                });
                await Task.Run(() => defaults.ApplyAsync(profile, plan, token, progress), token);
                ShowStatus("Pack defaults applied.", "The selected profile has the bundled mod configs and first-run defaults.",
                    "The previous config files are available under Restore backup.", "Check for updates", CheckAsync,
                    "Back", ShowSetup);
            });
}
