using System.IO;
using WzSync.Core;

namespace WzSync.App;

internal sealed record ProfileCandidate(string Name, string Path);

internal static class ProfileSelection
{
    internal static IReadOnlyList<ProfileCandidate> Discover(string instances)
    {
        var candidates = new List<ProfileCandidate>();
        try
        {
            RequireLocalDrive(instances);
            if (!Directory.Exists(instances)) return [];
            var root = new ProfilePaths(instances);
            foreach (var directory in Directory.EnumerateDirectories(root.Root))
            {
                try
                {
                    var profile = Open(directory);
                    candidates.Add(new ProfileCandidate(System.IO.Path.GetFileName(profile.Root), profile.Root));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    // A broken or inaccessible entry does not hide its valid siblings.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A custom local installation is still available through Browse.
        }
        return candidates.OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static ProfilePaths Open(string directory)
    {
        RequireLocalDrive(directory);
        var paths = new ProfilePaths(directory);
        var marker = Path.Combine(paths.Root, "minecraftinstance.json");
        if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Choose the CurseForge profile folder that contains minecraftinstance.json.");
        return paths;
    }

    private static void RequireLocalDrive(string directory)
    {
        var drive = Path.GetPathRoot(directory);
        if (!Path.IsPathFullyQualified(directory) || drive is not { Length: 3 } ||
            !char.IsAsciiLetter(drive[0]) || drive[1] != ':' || new DriveInfo(drive).DriveType == DriveType.Network)
            throw new InvalidDataException("Choose an existing CurseForge profile on a local drive.");
    }
}
