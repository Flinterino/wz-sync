namespace WzSync.Core;

public sealed class ProfilePaths
{
    public string Root { get; }
    public ProfilePaths(string root)
    {
        Root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        CheckRoot();
    }
    public void CheckRoot()
    {
        CheckAncestors(Root);
        if(!Directory.Exists(Root)) throw new InvalidDataException("Profile root must exist.");
    }
    public string ResolveManaged(string relative)
    {
        ManagedPolicy.ValidateFile(relative);
        return Resolve(relative);
    }
    internal string ResolveState(string relative)
    {
        ManagedPolicy.ValidateRelative(relative);
        return Resolve(".wzsync/"+relative);
    }
    internal string ResolveManagedDirectory(string relative)
    {
        if(relative!="mods") throw new InvalidDataException("Unapproved managed directory.");
        return Resolve(relative);
    }
    private string Resolve(string relative)
    {
        CheckRoot();
        var current=Root;
        foreach(var part in relative.Split('/'))
        {
            if(File.Exists(current)) throw new InvalidDataException("File used as path parent.");
            if(Directory.Exists(current))
            {
                var matches=Directory.EnumerateFileSystemEntries(current).Select(Path.GetFileName).Where(n=>string.Equals(n,part,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(matches.Length>1 || matches.Length==1 && matches[0]!=part) throw new InvalidDataException("Ambiguous local path casing.");
            }
            current=Path.Combine(current,part);
            CheckEntry(current);
        }
        return current;
    }
    internal static void CheckAncestors(string path)
    {
        var current=Path.GetPathRoot(path)!;
        CheckEntry(current);
        foreach(var part in path[current.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))
        {
            current=Path.Combine(current,part);
            CheckEntry(current);
        }
    }
    private static void CheckEntry(string path)
    {
        try
        {
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Reparse points are forbidden.");
        }
        catch(FileNotFoundException) { }
        catch(DirectoryNotFoundException) { }
    }
}

