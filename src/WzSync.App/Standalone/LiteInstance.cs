using System.IO;
using System.Text.Json;
using WzSync.Core;

namespace WzStandalone;

public static class LiteInstance
{
    public const string MinecraftVersion = "1.21.1";
    public const string LoaderVersion = "neoforge-21.1.265";
    private const int JavaMajor = 21;

    public static string Create(string localAppData) => Create(localAppData, null);

    public static string Import(string localAppData, string sourceRoot, Action<string>? configure = null) =>
        Create(localAppData, stage =>
        {
            LiteImport.CopyPinnedFiles(sourceRoot, stage, LiteImport.ReadPinnedFiles());
            configure?.Invoke(stage);
        });

    public static string GetPath(string localAppData)
    {
        if (!Path.IsPathFullyQualified(localAppData))
            throw new ArgumentException("An absolute local application data path is required.", nameof(localAppData));
        return Path.Combine(localAppData, "WarsAndZombiesStandalone", "Lite");
    }

    private static string Create(string localAppData, Action<string>? import)
    {
        var target = Path.GetFullPath(GetPath(localAppData));
        new ProfilePaths(localAppData).CheckRoot();
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var profileRoot = new ProfilePaths(parent);
        if (Directory.EnumerateFileSystemEntries(profileRoot.Root)
            .Any(path => string.Equals(Path.GetFileName(path), "Lite", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("A Lite instance already exists.");

        // Publish on the same volume so the final move either succeeds or leaves the destination alone.
        var stage = Directory.CreateDirectory(Path.Combine(profileRoot.Root, ".lite-" + Guid.NewGuid().ToString("N")));
        var metadata = Path.Combine(stage.FullName, "instance.json");
        try
        {
            using (var file = new FileStream(metadata, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, new
                {
                    schema = 1, pack = "lite", minecraft = MinecraftVersion, loader = LoaderVersion, javaMajor = JavaMajor, imported = import is not null,
                });
            Directory.CreateDirectory(Path.Combine(stage.FullName, "mods"));
            import?.Invoke(stage.FullName);
            profileRoot.CheckRoot();
            Directory.Move(stage.FullName, target);
            return target;
        }
        finally
        {
            if (stage.Exists)
            {
                // Delete only the staging directory created by this operation.
                profileRoot.CheckRoot();
                if (Path.GetDirectoryName(stage.FullName) != profileRoot.Root)
                    throw new InvalidDataException("Unexpected instance staging directory.");
                new ProfilePaths(stage.FullName).CheckRoot();
                Directory.Delete(stage.FullName, recursive: true);
            }
        }
    }

    public static string AssessUpdateFeed(SyncManifest manifest)
    {
        if (manifest.Pack != "lite")
            throw new InvalidDataException("Expected a Lite update manifest.");

        return $"Lite update feed {manifest.Version}: {manifest.Files.Length} managed files. " +
            "Fresh installation is blocked until a complete standalone Lite pack manifest is available.";
    }
}
