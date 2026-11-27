using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WzSync.Core;

namespace WzStandalone;

public sealed record LiteImportFile(string Path, string Sha256, long Size);

public static class LiteImport
{
    public static IReadOnlyList<LiteImportFile> ReadPinnedFiles()
    {
        using var stream = typeof(LiteImport).Assembly.GetManifestResourceStream("WzStandalone.lite-import.json")
            ?? throw new InvalidDataException("The Lite import file list is missing.");
        var files = JsonSerializer.Deserialize<List<LiteImportFile>>(stream)
            ?? throw new InvalidDataException("The Lite import file list is invalid.");
        ValidateFiles(files);
        return files.AsReadOnly();
    }

    public static void CopyPinnedFiles(string sourceRoot, string stagingRoot, IReadOnlyList<LiteImportFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!Path.IsPathFullyQualified(sourceRoot) || !Path.IsPathFullyQualified(stagingRoot))
            throw new ArgumentException("Lite import requires absolute source and staging directories.");

        var source = new ProfilePaths(sourceRoot);
        var staging = new ProfilePaths(stagingRoot);
        if (ContainsDirectory(source.Root, staging.Root) || ContainsDirectory(staging.Root, source.Root))
            throw new InvalidDataException("Lite import needs separate source and staging directories.");

        ValidateFiles(files);
        foreach (var file in files)
        {
            var input = source.ResolveManaged(file.Path);
            if (!File.Exists(input) || new FileInfo(input).Length != file.Size)
                throw new InvalidDataException($"The Lite source is missing the pinned file or has a different size: {file.Path}");
            var destination = staging.ResolveManaged(file.Path);
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException($"A pinned Lite file already exists in the staging directory: {file.Path}");
        }

        var buffer = new byte[128 * 1024];
        foreach (var file in files)
        {
            var input = source.ResolveManaged(file.Path);
            var destination = staging.ResolveManaged(file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            destination = staging.ResolveManaged(file.Path);

            using var from = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var to = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            int count;
            while ((count = from.Read(buffer)) != 0)
            {
                if (count > file.Size - total)
                    throw new InvalidDataException($"The Lite source file grew during import: {file.Path}");
                total += count;
                hash.AppendData(buffer, 0, count);
                to.Write(buffer, 0, count);
            }

            if (total != file.Size || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()),
                    file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The Lite source does not match the pinned bytes: {file.Path}");
        }
    }

    private static void ValidateFiles(IReadOnlyList<LiteImportFile> files)
    {
        if (files.Count == 0)
            throw new InvalidDataException("The Lite import file list is empty.");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file is null || string.IsNullOrEmpty(file.Path) || file.Sha256 is null
                || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) || file.Size < 0
                || !paths.Add(file.Path))
                throw new InvalidDataException("The Lite import file list has an invalid or duplicate file.");
            ManagedPolicy.ValidateFile(file.Path);
            if (file.Path.StartsWith("mods/", StringComparison.Ordinal)
                && !file.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only pinned JARs can be imported into mods.");
        }
    }

    private static bool ContainsDirectory(string parent, string child) =>
        string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
