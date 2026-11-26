using System.Collections.Immutable;
using System.Security.Cryptography;
namespace WzSync.Core;

public sealed record FileSnapshot(long Size,string Sha256)
{
    public static FileSnapshot Capture(string path)
    {
        ProfilePaths.CheckAncestors(Path.GetFullPath(path));
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var size=stream.Length;
        var hash=Convert.ToHexStringLower(SHA256.HashData(stream));
        if(stream.Position!=size || stream.Length!=size) throw new InvalidDataException("File changed while reading.");
        return new FileSnapshot(size,hash);
    }
    internal bool Matches(ManifestFile file) => Size==file.Size && Sha256==file.Sha256;
}
public enum PlanAction { Add, Update, Remove, Keep }
public sealed record PlanEntry
{
    public string Path { get; }
    public PlanAction Action { get; }
    public FileSnapshot? Current { get; }
    public ManifestFile? Desired { get; }
    internal PlanEntry(string path,PlanAction action,FileSnapshot? current,ManifestFile? desired)
        => (Path,Action,Current,Desired)=(path,action,current,desired);
}
public sealed class SyncPlan
{
    public string ProfileRoot { get; }
    public SyncManifest Manifest { get; }
    public ImmutableArray<PlanEntry> Entries { get; }
    public bool CanApply => !Entries.Any(e=>e.Action is PlanAction.Add or PlanAction.Update && e.Desired!.Availability=="manual");
    internal SyncPlan(ProfilePaths profile,SyncManifest manifest,IEnumerable<PlanEntry> entries)
        => (ProfileRoot,Manifest,Entries)=(profile.Root,manifest,entries.ToImmutableArray());
}
public static class SyncPlanner
{
    public static SyncPlan Create(ProfilePaths profile,SyncManifest manifest)
    {
        var desired=manifest.Files.ToDictionary(f=>f.Path,StringComparer.Ordinal);
        var entries=new List<PlanEntry>();
        foreach(var (path,current) in Inspect(profile,manifest.Managed,desired.Keys))
        {
            desired.TryGetValue(path,out var file);
            if(current==null && file==null) continue;
            var action=file==null ? PlanAction.Remove : current==null ? PlanAction.Add : current.Matches(file) ? PlanAction.Keep : PlanAction.Update;
            entries.Add(new PlanEntry(path,action,current,file));
        }
        return new SyncPlan(profile,manifest,entries);
    }

    internal static IEnumerable<(string Path,FileSnapshot? Current)> Inspect(ProfilePaths profile,IEnumerable<string> scope,IEnumerable<string> desiredPaths)
    {
        profile.CheckRoot();
        // Also refuse redirected internal state before offering an applicable plan.
        profile.ResolveState("tmp");
        var paths=new HashSet<string>(desiredPaths,StringComparer.Ordinal);
        foreach(var rule in scope)
        {
            ManagedPolicy.ValidateRule(rule);
            if(rule=="mods/")
            {
                var directory=profile.ResolveManagedDirectory("mods");
                if(File.Exists(directory)) throw new InvalidDataException("Managed directory is a file.");
                if(Directory.Exists(directory))
                {
                    var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        var relative="mods/"+Path.GetFileName(entry);
                        ManagedPolicy.ValidateFile(relative);
                        profile.ResolveManaged(relative);
                        if(!names.Add(relative)) throw new InvalidDataException("Local case collision.");
                        if(Directory.Exists(entry)) throw new InvalidDataException("Unexpected directory in managed mods.");
                        paths.Add(relative);
                    }
                }
            }
            else paths.Add(rule);
        }
        foreach(var path in paths.Order(StringComparer.Ordinal))
        {
            var full=profile.ResolveManaged(path);
            if(Directory.Exists(full)) throw new InvalidDataException("Managed file is a directory.");
            yield return (path,File.Exists(full) ? FileSnapshot.Capture(full) : null);
        }
    }
}
