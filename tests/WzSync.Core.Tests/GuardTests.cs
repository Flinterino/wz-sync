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
    [Fact] public void Existing_file_must_use_its_enumerated_name()
    {
        if(!OperatingSystem.IsWindows()) return;
        var mods=Path.Combine(root,"mods");
        Directory.CreateDirectory(mods);
        var name="An existing managed mod.jar";
        var file=Path.Combine(mods,name);
        File.WriteAllText(file,"fixture");
        var buffer=new System.Text.StringBuilder(32768);
        var length=GetShortPathName(file,buffer,buffer.Capacity);
        if(length==0 || length>=buffer.Capacity) return;
        var alternate=Path.GetFileName(buffer.ToString());
        if(string.Equals(alternate,name,StringComparison.OrdinalIgnoreCase)) return;
        var paths=new ProfilePaths(root);
        Assert.Equal(file,paths.ResolveManaged("mods/"+name));
        Assert.Throws<InvalidDataException>(()=>paths.ResolveManaged("mods/"+alternate));
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,EntryPoint="GetShortPathNameW",ExactSpelling=true)]
    private static extern uint GetShortPathName(string longPath,System.Text.StringBuilder shortPath,int capacity);

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
