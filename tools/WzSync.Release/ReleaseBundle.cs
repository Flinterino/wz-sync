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
                    ? extension == ".jar" && (row.Path.StartsWith("mods/wz_", StringComparison.Ordinal) || IsApprovedProjectJar(row.Path, row.Sha256))
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

    // The project owner approved these exact builds for the public pack feed.
    internal static bool IsApprovedProjectJar(string path, string sha256) => (path, sha256) switch
    {
        ("mods/opacnationsandwarfare-0.2.5-test.jar", "30633a2b43e56037fceb5ed8ee787277177dd916d9544da69655c6182e7207a5") => true,
        ("mods/seasonalfoliage-0.8.3.jar", "fd5d1f82b000959e4c1d641e17ad1eb566fb33b7c4c4f584b256bf7b6625a9dc") => true,
        ("mods/seasonalfoliage-0.9.1.jar", "6078287642b85c122fe247be784e0bf4e57f43178a936b01a0ebb3c1f6a3b39f") => true,
        _ => false
    };

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
