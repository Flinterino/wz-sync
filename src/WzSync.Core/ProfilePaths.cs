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
    public string? GetExistingLogDirectory()
    {
        var directory=Resolve(".wzsync");
        return Directory.Exists(directory) ? directory : null;
    }

    public Stream OpenSyncLog(string fileName)
    {
        ManagedPolicy.ValidateRelative(fileName);
        if(fileName.Contains('/') || !fileName.StartsWith("sync-",StringComparison.Ordinal) ||
            !fileName.EndsWith(".log",StringComparison.Ordinal))
            throw new InvalidDataException("Not a sync log name.");
        return new FileStream(ResolveState(fileName),FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
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
            var matches=Directory.Exists(current)
                ? Directory.EnumerateFileSystemEntries(current).Select(Path.GetFileName).Where(n=>string.Equals(n,part,StringComparison.OrdinalIgnoreCase)).ToArray()
                : [];
            if(matches.Length>1 || matches.Length==1 && matches[0]!=part) throw new InvalidDataException("Ambiguous local path casing.");
            current=Path.Combine(current,part);
            if(CheckEntry(current) && matches.Length==0) throw new InvalidDataException("Existing path does not use its directory entry name.");
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
    private static bool CheckEntry(string path)
    {
        try
        {
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Reparse points are forbidden.");
            return true;
        }
        catch(FileNotFoundException) { return false; }
        catch(DirectoryNotFoundException) { return false; }
    }
}
