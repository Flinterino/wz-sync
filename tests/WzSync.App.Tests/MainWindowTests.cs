using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using WzSync.Core;

namespace WzSync.App.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public Task Window_reviews_cancels_applies_and_restores_a_temporary_profile() => OnDispatcherAsync(async () =>
    {
        var profile = Directory.CreateTempSubdirectory("wz-sync-wpf-");
        try
        {
            File.WriteAllText(Path.Combine(profile.FullName, "minecraftinstance.json"), "{}");
            var mod = Path.Combine(profile.CreateSubdirectory("mods").FullName, "fixture.jar");
            File.WriteAllText(mod, "original fixture bytes");
            using var downloader = new VerifiedDownloader();
            var session = new SyncSession((_, _) => Task.FromResult(ManifestParser.Parse("""
                {"schema":1,"pack":"lite","version":"test-v1","minecraft":"1.21.1","loader":"neoforge-21.1.265","managed":["mods/"],"files":[]}
                """)), downloader);
            var window = new MainWindow(session, [new("Temporary profile", profile.FullName)]);
            try
            {
                var primary = Control<Button>(window, "PrimaryButton");
                var secondary = Control<Button>(window, "SecondaryButton");
                var grid = Control<DataGrid>(window, "ReviewGrid");
                Assert.Equal("Check for updates", primary.Content);
                Click(primary);
                await WaitUntilAsync(() => Equals(primary.Content, "Apply & Play"));
                Assert.Equal("REMOVE", Assert.IsType<ReviewRow>(Assert.Single(grid.Items)).Action);
                Control<CheckBox>(window, "ShowHashes").IsChecked = true;
                Assert.Equal(Visibility.Visible, grid.Columns[^2].Visibility);
                Click(secondary);
                Assert.True(File.Exists(mod));
                Assert.False(Directory.Exists(Path.Combine(profile.FullName, ".wzsync")));

                Click(primary);
                await WaitUntilAsync(() => Equals(primary.Content, "Apply & Play"));
                Click(primary);
                await WaitUntilAsync(() => Equals(primary.Content, "Play in CurseForge"));
                Assert.False(File.Exists(mod));

                Click(Control<Button>(window, "RestoreButton"));
                await WaitUntilAsync(() => Equals(primary.Content, "Restore backup"));
                Assert.Equal("ADD", Assert.IsType<ReviewRow>(Assert.Single(grid.Items)).Action);
                Assert.False(File.Exists(mod));
                Click(primary);
                await WaitUntilAsync(() => Equals(Control<TextBlock>(window, "StatusHeading").Text, "Backup restored."));
                Assert.Equal("Check for updates", primary.Content);
                Assert.Equal("original fixture bytes", File.ReadAllText(mod));
            }
            finally { window.Close(); }
        }
        finally { profile.Delete(true); }
    });

    [Fact]
    public Task Window_returns_to_setup_after_missing_release_and_can_check_again() => OnDispatcherAsync(async () =>
    {
        var profile = Directory.CreateTempSubdirectory("wz-sync-wpf-");
        try
        {
            File.WriteAllText(Path.Combine(profile.FullName, "minecraftinstance.json"), "{}");
            var mod = Path.Combine(profile.CreateSubdirectory("mods").FullName, "fixture.jar");
            File.WriteAllText(mod, "original fixture bytes");
            using var downloader = new VerifiedDownloader();
            var checkedPacks = new List<PackChoice>();
            const string missing = "The selected pack files are missing from the latest GitHub release.";
            var session = new SyncSession((pack, _) =>
            {
                checkedPacks.Add(pack);
                if (pack == PackChoice.Lite)
                    return Task.FromException<SyncManifest>(new InvalidDataException(missing));

                return Task.FromResult(ManifestParser.Parse("""
                {"schema":1,"pack":"full","version":"test-v1","minecraft":"1.21.1","loader":"neoforge-21.1.265","managed":["mods/"],"files":[]}
                """));
            }, downloader);
            var window = new MainWindow(session, [new("Temporary profile", profile.FullName)]);
            try
            {
                var primary = Control<Button>(window, "PrimaryButton");
                var secondary = Control<Button>(window, "SecondaryButton");
                var setup = Control<ScrollViewer>(window, "SetupPane");
                var profileList = Control<ListBox>(window, "ProfileList");

                Click(primary);
                await WaitUntilAsync(() => Control<TextBlock>(window, "StatusSubtitle").Text == missing);
                Assert.Equal("Check for updates", primary.Content);
                Click(primary);
                await WaitUntilAsync(() => Control<Border>(window, "StatusPane").Visibility == Visibility.Visible);
                Assert.Equal(new[] { PackChoice.Lite, PackChoice.Lite }, checkedPacks);
                Assert.Equal("Back", secondary.Content);
                Click(secondary);

                Assert.Equal(Visibility.Visible, setup.Visibility);
                Assert.Equal(0, profileList.SelectedIndex);
                Assert.True(Control<RadioButton>(window, "LitePack").IsChecked == true);
                Control<RadioButton>(window, "FullPack").IsChecked = true;
                Click(primary);
                await WaitUntilAsync(() => Equals(primary.Content, "Apply & Play"));
                Assert.Equal(new[] { PackChoice.Lite, PackChoice.Lite, PackChoice.Full }, checkedPacks);
                Assert.Equal("original fixture bytes", File.ReadAllText(mod));
                Assert.False(Directory.Exists(Path.Combine(profile.FullName, ".wzsync")));
            }
            finally { window.Close(); }
        }
        finally { profile.Delete(true); }
    });

    private static T Control<T>(Window window, string name) where T : class =>
        Assert.IsType<T>(window.FindName(name));

    private static void Click(Button button)
    {
        Assert.True(button.IsEnabled);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private static Task OnDispatcherAsync(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await work();
                    completion.TrySetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
