using WzSync.Core;
namespace WzSync.Core.Tests;
public sealed class GuardTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"wzsync-guard-"+Guid.NewGuid().ToString("N"));
    public GuardTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root,true);
    [Theory] [InlineData("mods")] [InlineData("config")]
    public void File_as_parent_is_rejected(string parent)
    {
        File.WriteAllText(Path.Combine(root,parent),"blocker");
        var path=parent=="mods" ? "mods/a.jar" : "config/DistantHorizons.toml";
        Assert.Throws<InvalidDataException>(()=>new ProfilePaths(root).ResolveManaged(path));
    }
    [Theory] [InlineData("profile")] [InlineData(".wzsync")]
    public void Junction_root_and_state_are_rejected(string name)
    {
        if(!OperatingSystem.IsWindows()) return;
        var target=Path.Combine(root,"target"); Directory.CreateDirectory(target); var link=Path.Combine(root,name);
        using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",$"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true });
        process!.WaitForExit(); Assert.Equal(0,process.ExitCode);
        try
        {
            Assert.Throws<InvalidDataException>(()=>SyncPlanner.Create(new ProfilePaths(name=="profile"?link:root),ManifestParser.Parse(ManifestTests.Json())));
        }
        finally { Directory.Delete(link); }
    }
    [Fact] public void Existing_local_case_collisions_are_rejected_on_case_sensitive_filesystem()
    {
        Directory.CreateDirectory(Path.Combine(root,"mods")); File.WriteAllText(Path.Combine(root,"mods/a.jar"),"a"); File.WriteAllText(Path.Combine(root,"mods/A.jar"),"A");
        if(Directory.GetFiles(Path.Combine(root,"mods")).Length!=2) return;
        Assert.Throws<InvalidDataException>(()=>SyncPlanner.Create(new ProfilePaths(root),ManifestParser.Parse(ManifestTests.Json())));
    }
    [Fact] public void Unmanaged_link_is_not_followed()
    {
        if(!OperatingSystem.IsWindows()) return;
        var link=Path.Combine(root,"saves"); var target=Path.Combine(root,"target"); Directory.CreateDirectory(target);
        using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",$"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true });
        process!.WaitForExit(); Assert.Equal(0,process.ExitCode);
        try { Assert.Empty(SyncPlanner.Create(new ProfilePaths(root),ManifestParser.Parse(ManifestTests.Json())).Entries); }
        finally { Directory.Delete(link); }
    }
}
