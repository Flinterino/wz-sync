using WzSync.Core;

namespace WzSync.Core.Tests;

public sealed class PackDefaultsTests
{
    [Fact]
    public async Task Applies_bundled_settings_and_can_restore_without_overwriting_personal_options()
    {
        var folder = Directory.CreateTempSubdirectory("wz-pack-defaults-");
        try
        {
            var profile = new ProfilePaths(folder.FullName);
            var personal = Path.Combine(profile.Root, "options.txt");
            File.WriteAllText(personal, "personal settings");
            var config = Directory.CreateDirectory(Path.Combine(profile.Root, "config"));
            var original = Path.Combine(config.FullName, "sodium-options.json");
            File.WriteAllText(original, "original config");
            var defaults = new PackDefaults();
            var manifest = defaults.Manifest(PackChoice.Lite);
            var plan = SyncPlanner.Create(profile, manifest);
            Assert.Contains(plan.Entries, row => row.Path == "config/defaultoptions/options.txt");
            Assert.Contains(plan.Entries, row => row.Path == "config/defaultoptions/keybindings.txt");
            await defaults.ApplyAsync(profile, plan, CancellationToken.None, null);
            Assert.All(SyncPlanner.Create(profile, manifest).Entries, row => Assert.Equal(PlanAction.Keep, row.Action));
            Assert.Equal("personal settings", File.ReadAllText(personal));
            Assert.DoesNotContain("lastServer", File.ReadAllText(Path.Combine(config.FullName, "defaultoptions", "options.txt")));

            using var downloader = new VerifiedDownloader();
            var engine = new TransactionEngine(profile, downloader);
            await engine.RestoreAsync(engine.PrepareRestoreLast());
            Assert.Equal("original config", File.ReadAllText(original));
            Assert.False(File.Exists(Path.Combine(config.FullName, "defaultoptions", "options.txt")));
            Assert.Equal("personal settings", File.ReadAllText(personal));
        }
        finally { folder.Delete(recursive: true); }
    }
}
