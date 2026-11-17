using WzSync.Core;
namespace WzSync.Core.Tests;
public sealed class PlannerTests : IDisposable
{
    private readonly string root=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"wzsync-test-"+Guid.NewGuid().ToString("N"));
    public PlannerTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root,true);
    [Fact] public void Planning_add_manual_blocks_and_writes_nothing()
    {
        var plan=SyncPlanner.Create(new ProfilePaths(root),ManifestParser.Parse(ManifestTests.Json(ManifestTests.Row())));
        Assert.Equal(PlanAction.Add,Assert.Single(plan.Entries).Action); Assert.False(plan.CanApply); Assert.Empty(Directory.GetFileSystemEntries(root));
    }
    [Fact] public void Plan_contains_keep_update_remove_and_preserves_unmanaged()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(root,"mods"));
        File.WriteAllText(System.IO.Path.Combine(root,"mods/a.jar"),"a");
        File.WriteAllText(System.IO.Path.Combine(root,"mods/old.jar"),"old");
        File.WriteAllText(System.IO.Path.Combine(root,"options.txt"),"personal");
        var snap=FileSnapshot.Capture(System.IO.Path.Combine(root,"mods/a.jar"));
        var json=ManifestTests.Json(ManifestTests.Row()).Replace(ManifestTests.Hash,snap.Sha256);
        var plan=SyncPlanner.Create(new ProfilePaths(root),ManifestParser.Parse(json));
        Assert.True(plan.CanApply); Assert.Contains(plan.Entries,e=>e.Action==PlanAction.Keep); Assert.Contains(plan.Entries,e=>e.Action==PlanAction.Remove);
        File.WriteAllText(System.IO.Path.Combine(root,"mods/a.jar"),"b");
        plan=SyncPlanner.Create(new ProfilePaths(root),ManifestParser.Parse(json)); Assert.Contains(plan.Entries,e=>e.Action==PlanAction.Update); Assert.False(plan.CanApply);
        Assert.Equal("personal",File.ReadAllText(System.IO.Path.Combine(root,"options.txt")));
    }
    [Fact] public void Narrowed_scope_never_removes_other_mods()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(root,"mods")); File.WriteAllText(System.IO.Path.Combine(root,"mods/old.jar"),"old");
        var plan=SyncPlanner.Create(new ProfilePaths(root),ManifestParser.Parse(ManifestTests.Json("","\"mods/a.jar\""))); Assert.Empty(plan.Entries);
    }
    [Fact] public void Wrong_case_local_path_rejected()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(root,"Mods"));
        Assert.Throws<InvalidDataException>(()=>SyncPlanner.Create(new ProfilePaths(root),ManifestParser.Parse(ManifestTests.Json())));
    }
    [Fact] public void Symlink_managed_parent_rejected()
    {
        var outside=System.IO.Path.Combine(root,"outside"); Directory.CreateDirectory(outside);
        try { Directory.CreateSymbolicLink(System.IO.Path.Combine(root,"mods"),outside); }
        catch(UnauthorizedAccessException) when(OperatingSystem.IsWindows()) { return; }
        Assert.Throws<InvalidDataException>(()=>new ProfilePaths(root).ResolveManaged("mods/a.jar"));
    }
    [Fact] public void Dangling_link_leaf_rejected()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(root,"mods"));
        try { File.CreateSymbolicLink(System.IO.Path.Combine(root,"mods/a.jar"),System.IO.Path.Combine(root,"missing")); }
        catch(UnauthorizedAccessException) when(OperatingSystem.IsWindows()) { return; }
        Assert.Throws<InvalidDataException>(()=>new ProfilePaths(root).ResolveManaged("mods/a.jar"));
    }
    [Fact] public void Windows_junction_rejected()
    {
        if(!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(System.IO.Path.Combine(root,"target"));
        using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",$"/c mklink /J \"{System.IO.Path.Combine(root,"mods")}\" \"{System.IO.Path.Combine(root,"target")}\"") { CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true });
        process!.WaitForExit(); Assert.Equal(0,process.ExitCode);
        try {
        Assert.Throws<InvalidDataException>(()=>new ProfilePaths(root).ResolveManaged("mods/a.jar"));
        } finally { Directory.Delete(System.IO.Path.Combine(root,"mods")); }
    }
}


