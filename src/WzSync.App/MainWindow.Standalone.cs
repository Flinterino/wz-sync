using System.IO;
using System.Windows;
using Microsoft.Win32;
using WzStandalone;
using WzSync.Core;

namespace WzSync.App;

public partial class MainWindow
{
    private bool IsStandalone => ProfileList.SelectedItem is ProfileCandidate { IsStandalone: true };
    private string PlayLabel => IsStandalone ? "Open Minecraft Launcher" : "Play in CurseForge";
    private string LaunchNote => IsStandalone ? "The official Minecraft Launcher handles sign-in and Play."
        : "Open CurseForge and press Play on the selected profile.";

    private void StandaloneSetup_Click(object sender, RoutedEventArgs args)
    {
        if (operation != null) return;
        if (StandaloneSetup.Profile is { } existing)
        {
            SelectStandalone(existing);
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Choose the existing Wars & Zombies Lite folder",
            InitialDirectory = (ProfileList.SelectedItem as ProfileCandidate)?.Path ?? "" };
        if (dialog.ShowDialog(this) != true) return;
        var source = dialog.FolderName;
        try
        {
            ProfileSelection.Open(source);
            var defaults = new PackDefaults().Manifest(PackChoice.Lite);
            var defaultsPaths = defaults.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = LiteImport.ReadPinnedFiles().Where(file => !defaultsPaths.Contains(file.Path)).ToArray();
            SwitchPane(ReviewPane);
            ReviewHeading.Text = "Review the Lite import";
            ReviewSubtitle.Text = "Copy verified Lite files and add pack settings for the first launch.";
            ReviewSummary.Text = $"{files.Length + defaults.Files.Length} files from {Path.GetFileName(source)} to {StandaloneSetup.Instance}";
            ReviewGrid.ItemsSource = files.Select(file => new ReviewRow("ADD", file.Path, "-", $"{file.Size:N0} B",
                "-", file.Sha256, "Local Lite profile", "", null, null)).Concat(defaults.Files.Select(file =>
                new ReviewRow("ADD", file.Path, "-", $"{file.Size:N0} B", "-", file.Sha256,
                    "Bundled pack defaults", "", null, null))).ToArray();
            ShowHashes.IsChecked = false;
            SetManualNotice(null);
            SetActions("Create Lite installation", () => ImportStandaloneAsync(source), "Back", ShowSetup,
                "The source profile is read-only during this import.");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ShowError(ex, "Could not prepare the Lite import.");
        }
    }

    private Task ImportStandaloneAsync(string source) => RunOperationAsync("Creating your Lite installation",
        "Copying and checking pinned files in a separate folder.", false, async _ =>
        {
            await Task.Run(() => LiteInstance.Import(StandaloneSetup.AppData, source, stage =>
            {
                var defaults = new PackDefaults();
                var profile = new ProfilePaths(stage);
                var plan = SyncPlanner.Create(profile, defaults.Manifest(PackChoice.Lite));
                defaults.ApplyAsync(profile, plan, CancellationToken.None, null).GetAwaiter().GetResult();
            }));
            SelectStandalone(StandaloneSetup.Profile ?? throw new InvalidDataException("The Lite installation is missing."));
        });

    private void SelectStandalone(ProfileCandidate candidate)
    {
        var match = profiles.FirstOrDefault(profile => profile.IsStandalone);
        if (match == null)
        {
            profiles.Add(candidate);
            match = candidate;
        }
        ProfileList.SelectedItem = match;
        // Imports finish inside the progress operation, where selection events are deferred.
        session.Select(match.Path, WzSync.Core.PackChoice.Lite, standalone: true);
        LitePack.IsChecked = true;
        FullPack.IsEnabled = false;
        ShowSetup();
        StartProfileCheck();
    }

    private Task OpenStandaloneAsync()
    {
        if (!StandaloneSetup.HasLoader)
        {
            ShowStatus("NeoForge setup required.", "Install NeoForge 21.1.265 in the official Minecraft Launcher before playing.",
                "The Lite files are ready. The installer opens separately so you can review its destination.",
                "Install NeoForge", InstallStandaloneLoaderAsync, "Back", ShowSetup);
            return Task.CompletedTask;
        }

        return RunOperationAsync("Opening Minecraft Launcher", "Preparing the standalone Lite installation.", false, _ =>
        {
            StandaloneSetup.RequireLauncherClosed();
            OfficialLauncherProfile.Register(StandaloneSetup.OfficialRoot, StandaloneSetup.Instance);
            StandaloneSetup.OpenOfficialLauncher();
            ShowStatus("Minecraft Launcher opened.", $"Select {OfficialLauncherProfile.ProfileName} and press Play.",
                null, null, null, "Back", ShowSetup);
            return Task.CompletedTask;
        });
    }

    private Task InstallStandaloneLoaderAsync()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var java = Path.Combine(user, "curseforge", "minecraft", "Install", "runtime", "java-runtime-delta",
            "windows-x64", "java-runtime-delta", "bin", "javaw.exe");
        if (!File.Exists(java))
        {
            var dialog = new OpenFileDialog { Title = "Choose Java 21", Filter = "Java executable|java.exe;javaw.exe" };
            if (dialog.ShowDialog(this) != true) return Task.CompletedTask;
            java = dialog.FileName;
        }

        return RunOperationAsync("Installing NeoForge", "Choose Install client and keep the default Minecraft directory.", false,
            async token =>
            {
                StandaloneSetup.RequireLauncherClosed();
                var root = StandaloneSetup.OfficialRoot;
                if (!File.Exists(Path.Combine(root, "launcher_profiles.json")) &&
                    !File.Exists(Path.Combine(root, "launcher_profiles_microsoft_store.json")))
                    throw new InvalidDataException("Open the official Minecraft Launcher once and sign in before installing NeoForge.");
                using var process = await OfficialNeoForgeInstaller.StartAsync(java,
                    Path.GetDirectoryName(StandaloneSetup.Instance)!, token);
                await process.WaitForExitAsync(token);
                if (!StandaloneSetup.HasLoader)
                    throw new InvalidDataException("NeoForge 21.1.265 was not found in the official Minecraft directory.");
                ShowStatus("NeoForge is ready.", "Open Minecraft Launcher to play the standalone Lite installation.", null,
                    "Check for updates", CheckAsync, "Back", ShowSetup);
            });
    }
}
