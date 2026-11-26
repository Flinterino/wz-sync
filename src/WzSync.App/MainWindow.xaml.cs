using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WzSync.Core;

namespace WzSync.App;

public partial class MainWindow : Window
{
    private readonly ReleaseManifestClient releases = new();
    private readonly VerifiedDownloader downloader = new();
    private readonly SyncSession session;
    private readonly ObservableCollection<ProfileCandidate> profiles;
    private Func<Task>? primaryAction;
    private Action? secondaryAction;
    private CancellationTokenSource? operation;
    private string? activeFile;
    private bool initialized;

    public MainWindow() : this(null, null) { }

    internal MainWindow(SyncSession? fixtureSession, IReadOnlyList<ProfileCandidate>? fixtureProfiles)
    {
        session = fixtureSession ?? new SyncSession(releases.FetchAsync, downloader);
        profiles = new ObservableCollection<ProfileCandidate>(fixtureProfiles ?? DiscoverProfiles());
        InitializeComponent();
        ProfileList.ItemsSource = profiles;
        initialized = true;
        if (profiles.Count == 1) ProfileList.SelectedIndex = 0;
        else ShowSetup();
    }

    private static IReadOnlyList<ProfileCandidate> DiscoverProfiles()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(user) ? [] :
            ProfileSelection.Discover(Path.Combine(user, "curseforge", "minecraft", "Instances"));
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs args) => SelectProfile();
    private void Pack_Changed(object sender, RoutedEventArgs args) => SelectProfile();

    private void SelectProfile()
    {
        if (!initialized || operation != null) return;
        var profile = ProfileList.SelectedItem as ProfileCandidate;
        try
        {
            session.Select(profile?.Path, FullPack.IsChecked == true ? PackChoice.Full : PackChoice.Lite);
            ShowSetup();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ShowError(ex, "Couldn’t select that profile.");
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose your CurseForge profile folder",
            InitialDirectory = session.Profile?.Root ?? ""
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var profile = ProfileSelection.Open(dialog.FolderName);
            var candidate = profiles.FirstOrDefault(item =>
                string.Equals(item.Path, profile.Root, StringComparison.OrdinalIgnoreCase));
            if (candidate == null)
            {
                candidate = new ProfileCandidate(Path.GetFileName(profile.Root), profile.Root);
                profiles.Add(candidate);
            }
            ProfileList.SelectedItem = candidate;
            SelectProfile();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            session.DiscardReview();
            ShowError(ex, "Couldn’t select that profile.");
        }
    }

    private void ShowSetup()
    {
        session.DiscardReview();
        activeFile = null;
        ReviewGrid.ItemsSource = null;
        SwitchPane(SetupPane);
        ProfileList.Visibility = profiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ProfileHint.Text = session.Profile?.Root ?? (profiles.Count == 0
            ? "No profile found in the default location. Browse to your existing profile."
            : "Select the profile you want to sync.");
        SetActions("Check for updates", session.Profile != null ? CheckAsync : null,
            null, null, "Only managed files in the selected profile can change.");
        UpdateAvailability();
    }

    private Task CheckAsync() => RunOperationAsync("Checking for updates",
        "Reading the latest release and checking the selected profile.", true,
        async token => ShowSyncReview(await session.CheckAsync(token)));

    private void ShowSyncReview(SyncPlan plan)
    {
        var changed = plan.Entries.Count(row => row.Action != PlanAction.Keep);
        var manual = plan.Entries.Count(row => (row.Action is PlanAction.Add or PlanAction.Update) &&
            row.Desired?.Availability == "manual");
        SwitchPane(ReviewPane);
        ReviewHeading.Text = changed == 0 ? "You’re up to date." : "Review the change list";
        ReviewSubtitle.Text = changed == 0
            ? "Your managed files matched the release when checked."
            : "Review every file before approving the update.";
        ReviewSummary.Text = $"{ProfileName}  ·  {session.Pack}  ·  Installed version unknown  →  {plan.Manifest.Version}  ·  {ChangeCount(changed)}";
        ReviewGrid.ItemsSource = plan.Entries.Select(ReviewRow.FromPlan).ToArray();
        ShowHashes.IsChecked = false;
        SetManualNotice(manual > 0
            ? (manual == 1 ? "One required file needs a manual update. Update it in CurseForge, then check again."
                : $"{manual} required files need a manual update. Update them in CurseForge, then check again.")
            : null);
        if (manual > 0)
            SetActions("Check again", CheckAsync, "Cancel", ShowSetup, "Sync waits until all required files match.");
        else if (changed == 0)
            SetActions("Play in CurseForge", () => VerifyAndPlayAsync(plan), "Back", ShowSetup,
                "Game launch stays in CurseForge.");
        else
            SetActions("Apply & Play", () => ApplyAsync(plan, changed), "Cancel", ShowSetup,
                "Keep Minecraft closed while the update runs.");
    }

    private Task ApplyAsync(SyncPlan plan, int changed) => RunOperationAsync("Downloading and checking files",
        "Every payload is verified before the app updates the profile.", true, async token =>
        {
            var progress = new Progress<SyncProgress>(report =>
            {
                if (operation?.Token == token) ShowSyncProgress(report);
            });
            await session.ApplyAsync(plan, token, progress);
            ShowStatus("Sync complete.", "Open CurseForge and press Play on the selected profile.",
                $"{ProfileName}  ·  {session.Pack}  ·  {plan.Manifest.Version}\n{ChangeCount(changed)} applied. A local backup is available.",
                "Play in CurseForge", () => VerifyAndPlayAsync(plan), "Close", Close);
        });

    private void ShowSyncProgress(SyncProgress report)
    {
        var applying = report.Phase == SyncPhase.Applying;
        activeFile = report.Path;
        ProgressHeading.Text = applying ? "Backing up and applying" : "Downloading and checking files";
        ProgressSubtitle.Text = applying
            ? "Keep this window open until the approved changes finish."
            : "The current managed files stay in place until every download is verified.";
        ProgressMeter.IsIndeterminate = false;
        ProgressMeter.Maximum = Math.Max(1, report.Total);
        ProgressMeter.Value = report.Completed;
        var file = report.Path == null ? "" : "\n" + report.Path;
        ProgressFile.Text = $"{report.Completed} of {report.Total} {(applying ? "changes applied" : "files checked")}{file}";
        if (applying) SetActions(null, null, null, null, "The approved transaction is applying.");
    }

    private async Task VerifyAndPlayAsync(SyncPlan plan)
    {
        var verified = false;
        await RunOperationAsync("Checking managed files", "Making sure the profile still matches the release.",
            true, async token =>
            {
                await session.VerifyCurrentAsync(plan, token);
                verified = true;
            });
        if (verified) await PlayAsync();
    }

    private Task PlayAsync()
    {
        MessageBox.Show(this, $"Open CurseForge, choose “{ProfileName}” and press Play.", "Play in CurseForge",
            MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
        return Task.CompletedTask;
    }

    private async void PrepareRestore_Click(object sender, RoutedEventArgs args) =>
        await RunOperationAsync("Checking the last backup", "Preparing a list of every change the restore will make.",
            false, async _ => ShowRestoreReview(await session.PrepareRestoreAsync()));

    private void ShowRestoreReview(RestorePlan plan)
    {
        SwitchPane(ReviewPane);
        ReviewHeading.Text = "Restore your last backup";
        ReviewSubtitle.Text = "Review every change before restoring. Current versions are backed up first.";
        ReviewSummary.Text = $"{ProfileName}  ·  {ChangeCount(plan.Entries.Count(row => row.Action != PlanAction.Keep))} to restore";
        ReviewGrid.ItemsSource = plan.Entries.Select(ReviewRow.FromRestore).ToArray();
        ShowHashes.IsChecked = false;
        SetManualNotice(null);
        SetActions("Restore backup", () => RestoreAsync(plan), "Cancel", ShowSetup,
            "Restoring changes the selected profile. Check for updates again before playing.");
    }

    private Task RestoreAsync(RestorePlan plan) => RunOperationAsync("Restoring the last backup",
        "Saving the current files and restoring the reviewed versions.", false, async _ =>
        {
            await session.RestoreAsync(plan);
            ShowStatus("Backup restored.", "Check for updates before joining the server.",
                "Current versions were saved to a local backup.", "Check for updates", CheckAsync, "Close", Close);
        });

    private async Task RecoverAsync()
    {
        if (MessageBox.Show(this, "Recover the previous managed files recorded by the interrupted sync?",
                "Recover interrupted sync", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunOperationAsync("Recovering the interrupted sync", "Checking the journal and preserving local backups.",
            false, async _ =>
            {
                var recovered = await session.RecoverAsync();
                ShowStatus(recovered ? "Recovery complete." : "No interrupted sync found.",
                    "Check the selected profile again before playing.", null, "Check for updates", CheckAsync,
                    "Back", ShowSetup);
            });
    }

    private async Task RunOperationAsync(string title, string description, bool cancellable,
        Func<CancellationToken, Task> work)
    {
        if (operation != null) return;
        using var cancel = new CancellationTokenSource();
        operation = cancel;
        activeFile = null;
        SwitchPane(ProgressPane);
        ProgressHeading.Text = title;
        ProgressSubtitle.Text = description;
        ProgressFile.Text = "";
        ProgressMeter.IsIndeterminate = true;
        SetActions(null, null, cancellable ? "Cancel" : null, cancellable ? RequestCancel : null,
            "Leave the selected profile unchanged while the app is working.");
        UpdateAvailability();
        try
        {
            await work(cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            ShowStatus("Canceled.", "No managed files were changed.", null, "Check for updates", CheckAsync,
                "Back", ShowSetup);
        }
        catch (RecoveryRequiredException)
        {
            ShowStatus("Recovery needed.", "An earlier sync was interrupted. Review the log or recover before checking again.",
                null, "Recover interrupted sync", RecoverAsync, "Back", ShowSetup, isError: true);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            ShowError(ex, "Couldn’t finish this operation.");
        }
        finally
        {
            operation = null;
            UpdateAvailability();
        }
    }

    private void RequestCancel()
    {
        operation?.Cancel();
        SecondaryButton.IsEnabled = false;
        FooterNote.Text = "Cancel requested. Waiting for the current step.";
    }

    private void ShowError(Exception ex, string title)
    {
        var reason = ex switch
        {
            InvalidDataException => ex.Message,
            InvalidOperationException => ex.Message,
            UnauthorizedAccessException => "Windows denied access to the selected files.",
            IOException when ex.Message is "Could not reach GitHub to check for updates." or "The release request timed out."
                => ex.Message,
            _ => "A file or network request could not be completed. Close Minecraft and check the local log."
        };
        var detail = activeFile == null ? null : $"File: {activeFile}";
        var canRetry = session.Profile != null;
        ShowStatus(title, reason, detail, canRetry ? "Check for updates" : null,
            canRetry ? CheckAsync : null, "Back", ShowSetup, isError: true);
    }

    private void ShowStatus(string heading, string subtitle, string? detail, string? primary, Func<Task>? action,
        string secondary, Action back, bool isError = false)
    {
        SwitchPane(StatusPane);
        StatusHeading.Text = heading;
        StatusHeading.Foreground = (Brush)FindResource(isError ? "Red" : "Ink");
        StatusSubtitle.Text = subtitle;
        StatusDetails.Text = detail ?? "";
        StatusDetails.Visibility = detail == null ? Visibility.Collapsed : Visibility.Visible;
        SetActions(primary, action, secondary, back, "Game launch stays in CurseForge.");
    }

    private void SwitchPane(UIElement shown)
    {
        SetupPane.Visibility = shown == SetupPane ? Visibility.Visible : Visibility.Collapsed;
        ReviewPane.Visibility = shown == ReviewPane ? Visibility.Visible : Visibility.Collapsed;
        ProgressPane.Visibility = shown == ProgressPane ? Visibility.Visible : Visibility.Collapsed;
        StatusPane.Visibility = shown == StatusPane ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetActions(string? primary, Func<Task>? action, string? secondary, Action? back, string note)
    {
        primaryAction = action;
        secondaryAction = back;
        PrimaryButton.Content = primary;
        PrimaryButton.Visibility = primary == null ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.IsEnabled = action != null;
        SecondaryButton.Content = secondary;
        SecondaryButton.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryButton.IsEnabled = back != null;
        FooterNote.Text = note;
    }

    private void UpdateAvailability()
    {
        var ready = operation == null && session.Profile != null;
        RestoreButton.IsEnabled = ready;
        ViewLogButton.IsEnabled = ready;
        OpenLogFolderButton.IsEnabled = ready;
    }

    private void SetManualNotice(string? text)
    {
        ManualNotice.Text = text ?? "";
        ManualNotice.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HashVisibility_Changed(object sender, RoutedEventArgs args)
    {
        if (OldHashColumn is null || NewHashColumn is null) return;
        var visibility = ShowHashes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OldHashColumn.Visibility = visibility;
        NewHashColumn.Visibility = visibility;
    }

    private async void Primary_Click(object sender, RoutedEventArgs args)
    {
        if (operation == null && primaryAction != null) await primaryAction();
    }

    private void Secondary_Click(object sender, RoutedEventArgs args) => secondaryAction?.Invoke();

    private void OpenSource_Click(object sender, RoutedEventArgs args) =>
        OpenExternal("https://github.com/Flinterino/wz-sync");

    private void OpenModPage_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: ReviewRow row } &&
            PublicLinks.GetModPage(row.Provider, row.Page) is Uri page)
            OpenExternal(page.AbsoluteUri);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var directory = session.Profile?.GetExistingLogDirectory();
            if (directory == null) Inform("The selected profile does not have a sync log folder yet.");
            else OpenExternal(directory);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Inform("The selected profile’s log folder could not be opened.");
        }
    }

    private void ViewLog_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var profile = session.Profile;
            var folder = profile?.GetExistingLogDirectory();
            var name = folder == null ? null : Directory.EnumerateFiles(folder, "sync-*.log")
                .Select(Path.GetFileName).OrderDescending(StringComparer.Ordinal).FirstOrDefault();
            if (profile == null || name == null)
            {
                Inform("The selected profile does not have a sync log yet.");
                return;
            }
            using var reader = new StreamReader(profile.OpenSyncLog(name));
            var buffer = new char[256 * 1024];
            var length = reader.ReadBlock(buffer, 0, buffer.Length);
            var content = new string(buffer, 0, length);
            if (reader.Peek() >= 0) content += "\n\nUse Open log folder to read the rest of this log.";
            new Window
            {
                Title = "Latest sync log", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 790, Height = 470, Background = (Brush)FindResource("Paper"),
                Content = new TextBox
                {
                    Text = content, Margin = new Thickness(14), Padding = new Thickness(10),
                    Background = (Brush)FindResource("Surface"), Foreground = (Brush)FindResource("Ink"),
                    FontFamily = new FontFamily("Consolas"), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }
            }.ShowDialog();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Inform("The selected profile’s sync log could not be read.");
        }
    }

    private void OpenExternal(string destination)
    {
        try { using var process = Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Inform("Windows could not open the requested location.");
        }
    }

    private void Inform(string message) =>
        MessageBox.Show(this, message, "Wars & Zombies Sync", MessageBoxButton.OK, MessageBoxImage.Information);

    private static string ChangeCount(int count) => $"{count} {(count == 1 ? "change" : "changes")}";

    private string ProfileName => session.Profile == null ? "Selected profile" : Path.GetFileName(session.Profile.Root);

    private static bool IsExpected(Exception ex) =>
        ex is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (operation != null)
        {
            e.Cancel = true;
            FooterNote.Text = "Wait for the current operation to finish.";
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        downloader.Dispose();
        releases.Dispose();
        base.OnClosed(e);
    }
}
