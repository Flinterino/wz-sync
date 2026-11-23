using WzSync.Core;

namespace WzSync.Release;

internal static class ReleaseBundle
{
    internal static int Validate(string directory, string tag)
    {
        var root = new ProfilePaths(directory).Root;
        var inventory = new Dictionary<string, string>(StringComparer.Ordinal);
        var caseInsensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(path);
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !caseInsensitiveNames.Add(name))
                throw new InvalidDataException("Release input must contain only uniquely named regular files.");
            inventory.Add(name, path);
        }

        var owned = new Dictionary<string, FileSnapshot>(StringComparer.Ordinal);
        foreach (var pack in new[] { PackChoice.Full, PackChoice.Lite })
        {
            var name = pack == PackChoice.Full ? "manifest-full.json" : "manifest-lite.json";
            var manifest = ReleaseManifestClient.VerifyManifest(
                ReadSmallFile(Take(inventory, name), ManifestParser.MaximumBytes),
                ReadSmallFile(Take(inventory, name + ".sha256"), ReleaseManifestClient.MaximumChecksumBytes),
                pack, tag);
            if (manifest.Files.IsEmpty) throw new InvalidDataException("A release pack has no managed files.");
            foreach (var row in manifest.Files.Where(file => file.Source == "github"))
            {
                var extension = Path.GetExtension(row.Path);
                var allowedExtension = row.Path.StartsWith("mods/", StringComparison.Ordinal)
                    ? row.Path.StartsWith("mods/wz_", StringComparison.Ordinal) && extension == ".jar"
                    : extension is ".json" or ".toml" or ".properties";
                if (row.Availability != "download" || !allowedExtension)
                    throw new InvalidDataException("An owned asset has an unsupported managed path or availability.");

                var asset = row.Sha256 + extension;
                var expected = new FileSnapshot(row.Size, row.Sha256);
                if (owned.TryGetValue(asset, out var recorded))
                {
                    if (recorded != expected) throw new InvalidDataException("Pack manifests disagree about an owned asset.");
                }
                else
                {
                    owned.Add(asset, expected);
                }
            }
        }

        var bundleCount = inventory.Count;
        if (bundleCount != owned.Count || !owned.Keys.All(inventory.ContainsKey))
            throw new InvalidDataException("The bundle must contain exactly the referenced owned assets.");
        foreach (var (name, expected) in owned)
        {
            var path = inventory[name];
            if (new FileInfo(path).Length != expected.Size || FileSnapshot.Capture(path) != expected)
                throw new InvalidDataException("An owned release asset failed its SHA-256 or byte-size check.");
        }
        return bundleCount;
    }

    private static string Take(Dictionary<string, string> inventory, string name)
    {
        if (!inventory.Remove(name, out var path))
            throw new InvalidDataException($"The bundle is missing {name}.");
        return path;
    }

    private static byte[] ReadSmallFile(string path, int max)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > max) throw new InvalidDataException("A manifest or checksum exceeds its size limit.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("A release input changed during validation.");
        return bytes;
    }
}
