using System.IO;
using WzSync.Core;

namespace WzSync.App.Tests;

public sealed class SyncSessionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "wz-sync-app-" + Guid.NewGuid().ToString("N"));
    private readonly VerifiedDownloader downloader = new();

    public SyncSessionTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "minecraftinstance.json"), "{}");
    }

    [Fact]
    public async Task Check_and_cancel_leave_profile_unchanged_and_invalidate_the_approval()
    {
        var file = PutMod();
        var session = Session();
        var plan = await session.CheckAsync(default);
        Assert.Equal(PlanAction.Remove, Assert.Single(plan.Entries).Action);
        Assert.Equal("original", File.ReadAllText(file));
        Assert.False(Directory.Exists(Path.Combine(root, ".wzsync")));
        session.DiscardReview();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyAsync(plan, default));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Pack_selection_and_new_check_invalidate_the_old_object()
    {
        var file = PutMod();
        var session = Session();
        var plan = await session.CheckAsync(default);
        session.Select(root, PackChoice.Lite);
        var fresh = await session.CheckAsync(default);
        Assert.Equal("lite", fresh.Manifest.Pack);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyAsync(plan, default));
        Assert.True(File.Exists(file));
        Assert.False(Directory.Exists(Path.Combine(root, ".wzsync")));
    }

    [Fact]
    public async Task Manual_requirement_blocks_apply_and_reports_the_file()
    {
        var session = Session(manual: true);
        var plan = await session.CheckAsync(default);
        Assert.False(plan.CanApply);
        Assert.Equal("mods/manual.jar", Assert.Single(plan.Entries).Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyAsync(plan, default));
        Assert.False(Directory.Exists(Path.Combine(root, ".wzsync")));
    }

    [Fact]
    public async Task Core_rejects_a_stale_on_disk_plan_and_preserves_the_edit()
    {
        var file = PutMod();
        var session = Session();
        var plan = await session.CheckAsync(default);
        File.WriteAllText(file, "user edit");
        await Assert.ThrowsAsync<InvalidDataException>(() => session.ApplyAsync(plan, default));
        Assert.Equal("user edit", File.ReadAllText(file));
        Assert.False(Directory.Exists(Path.Combine(root, ".wzsync")));
        Assert.Null(session.SyncReview);
    }

    [Fact]
    public async Task Approved_apply_and_restore_round_trip_and_clear_the_review()
    {
        var file = PutMod();
        var session = Session();
        var plan = await session.CheckAsync(default);
        await session.ApplyAsync(plan, default);
        Assert.False(File.Exists(file));
        Assert.Null(session.SyncReview);

        var first = await session.PrepareRestoreAsync();
        var restore = await session.PrepareRestoreAsync();
        Assert.Equal(PlanAction.Add, Assert.Single(restore.Entries).Action);
        Assert.False(File.Exists(file));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RestoreAsync(first));
        Assert.Null(session.RestoreReview);
        await session.RestoreAsync(await session.PrepareRestoreAsync());
        Assert.Equal("original", File.ReadAllText(file));
        Assert.Null(session.SyncReview);
        Assert.Null(session.RestoreReview);
    }

    [Fact]
    public async Task Play_rechecks_files_after_a_successful_sync()
    {
        var file = PutMod();
        var session = Session();
        var plan = await session.CheckAsync(default);
        await session.ApplyAsync(plan, default);
        Assert.False(File.Exists(file));

        File.WriteAllText(file, "another change");
        await Assert.ThrowsAsync<InvalidDataException>(() => session.VerifyCurrentAsync(plan, default));
        Assert.Equal("another change", File.ReadAllText(file));
    }

    [Fact]
    public async Task Selection_restore_and_duplicate_check_are_blocked_during_check()
    {
        var response = new TaskCompletionSource<SyncManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new SyncSession((_, token) => response.Task.WaitAsync(token), downloader);
        session.Select(root, PackChoice.Full);
        using var cancel = new CancellationTokenSource();
        var pending = session.CheckAsync(cancel.Token);
        Assert.Throws<InvalidOperationException>(() => session.Select(root, PackChoice.Lite));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CheckAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PrepareRestoreAsync());
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(session.IsBusy);
        Assert.Null(session.SyncReview);
        Assert.False(Directory.Exists(Path.Combine(root, ".wzsync")));
    }

    [Fact]
    public void Discovers_only_immediate_instance_folders_and_never_creates_state()
    {
        var instances = Directory.CreateDirectory(Path.Combine(root, "Instances")).FullName;
        var profile = Directory.CreateDirectory(Path.Combine(instances, "Test profile")).FullName;
        var unrelated = Directory.CreateDirectory(Path.Combine(instances, "Other")).FullName;
        File.WriteAllText(Path.Combine(profile, "minecraftinstance.json"), "{}");
        var candidate = Assert.Single(ProfileSelection.Discover(instances));
        Assert.Equal(profile, candidate.Path);
        Assert.Equal("Test profile", candidate.Name);
        Assert.Throws<InvalidDataException>(() => ProfileSelection.Open(unrelated));
        Assert.False(Directory.Exists(Path.Combine(profile, ".wzsync")));
    }

    [Theory]
    [InlineData("curseforge", "https://www.curseforge.com/minecraft/mc-mods/fixture", true)]
    [InlineData("curseforge", "https://www.curseforge.com/projects/123", true)]
    [InlineData("modrinth", "https://modrinth.com/mod/fixture", true)]
    [InlineData("github", "https://github.com/Flinterino/wz-sync", true)]
    [InlineData("github", "https://github.com/other/repo", false)]
    [InlineData("modrinth", "https://www.curseforge.com/minecraft/mc-mods/fixture", false)]
    [InlineData("curseforge", "https://curseforge.com.evil.test/minecraft/mc-mods/fixture", false)]
    [InlineData("curseforge", "https://www.curseforge.com/minecraft/mc-mods/fixture?a=one", false)]
    [InlineData("curseforge", "file:///fixture", false)]
    public void Mod_page_belongs_to_the_selected_provider(string source, string url, bool allowed) =>
        Assert.Equal(allowed, PublicLinks.GetModPage(source, new Uri(url)) != null);

    private SyncSession Session(bool manual = false)
    {
        var session = new SyncSession((pack, _) => Task.FromResult(Manifest(pack, manual)), downloader);
        session.Select(root, PackChoice.Full);
        return session;
    }

    private string PutMod()
    {
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        var file = Path.Combine(root, "mods", "fixture.jar");
        File.WriteAllText(file, "original");
        return file;
    }

    private static SyncManifest Manifest(PackChoice pack, bool manual)
    {
        var row = manual
            ? """{"path":"mods/manual.jar","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1,"source":"curseforge","url":null,"page":"https://www.curseforge.com/minecraft/mc-mods/fixture","availability":"manual","reason":"Update in CurseForge"}"""
            : "";
        return ManifestParser.Parse($$"""
            {"schema":1,"pack":"{{(pack == PackChoice.Full ? "full" : "lite")}}","version":"test-v1","minecraft":"1.21.1","loader":"neoforge-21.1.265","managed":["mods/"],"files":[{{row}}]}
            """);
    }

    public void Dispose()
    {
        downloader.Dispose();
        Directory.Delete(root, true);
    }
}
