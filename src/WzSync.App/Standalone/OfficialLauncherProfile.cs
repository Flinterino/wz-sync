using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using WzSync.Core;

namespace WzStandalone;

public static class OfficialLauncherProfile
{
    public const string ProfileId = "wars-and-zombies-lite";
    public const string ProfileName = "Wars & Zombies Lite (WzSync)";

    public static string? Register(string launcherRoot, string instanceRoot)
    {
        var launcher = new ProfilePaths(launcherRoot);
        var instance = new ProfilePaths(instanceRoot);
        var versionDirectory = Path.Combine(launcher.Root, "versions", LiteInstance.LoaderVersion);
        var versionFile = Path.Combine(versionDirectory, LiteInstance.LoaderVersion + ".json");
        if (!File.Exists(versionFile))
            throw new InvalidDataException("NeoForge 21.1.265 is missing from the official launcher. Run install-neoforge first.");
        new ProfilePaths(versionDirectory).CheckRoot();
        var manifest = ReadObject(ReadLocalFile(versionFile), "NeoForge version");
        if (manifest["id"]?.GetValue<string>() != LiteInstance.LoaderVersion ||
            manifest["inheritsFrom"]?.GetValue<string>() != LiteInstance.MinecraftVersion)
            throw new InvalidDataException("Install the pinned NeoForge version in the official launcher first.");

        var storeFile = Path.Combine(launcher.Root, "launcher_profiles_microsoft_store.json");
        var path = File.Exists(storeFile) ? storeFile : Path.Combine(launcher.Root, "launcher_profiles.json");
        var original = ReadLocalFile(path);
        var document = ReadObject(original, "Minecraft Launcher profile file");
        if (document["profiles"] is not JsonObject profiles)
            throw new InvalidDataException("The official Minecraft Launcher profile file has no profiles object.");

        JsonObject profile;
        if (profiles.ContainsKey(ProfileId))
        {
            profile = profiles[ProfileId] as JsonObject ?? throw new InvalidDataException("Invalid existing Lite profile.");
            if (profile["name"]?.GetValue<string>() != ProfileName ||
                profile["gameDir"]?.GetValue<string>() != instance.Root)
                throw new InvalidDataException("The existing Lite profile points elsewhere. Rename or remove it in the official launcher first.");
            if (profile["lastVersionId"]?.GetValue<string>() == LiteInstance.LoaderVersion)
                return null;
        }
        else
        {
            profile = new JsonObject
            {
                ["name"] = ProfileName,
                ["type"] = "custom",
                ["gameDir"] = instance.Root,
            };
            profiles.Add(ProfileId, profile);
        }
        profile["lastVersionId"] = LiteInstance.LoaderVersion;

        var suffix = ".wzsync-" + Guid.NewGuid().ToString("N");
        var temporary = path + suffix + ".tmp";
        var backup = path + suffix + ".bak";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            launcher.CheckRoot();
            if (!ReadLocalFile(path).AsSpan().SequenceEqual(original))
                throw new IOException("The launcher profile changed during setup. Close the launcher before trying again.");
            File.Replace(temporary, path, backup);
            return backup;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static JsonObject ReadObject(byte[] data, string label)
    {
        try
        {
            // Existing launcher files can start with a UTF-8 BOM.
            ReadOnlySpan<byte> json = data;
            if (json.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
                json = json[3..];
            return JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException($"Invalid {label}.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Invalid {label}.", error);
        }
    }

    private static byte[] ReadLocalFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked launcher files are not supported.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Launcher file is too large.");
        var result = new byte[(int)stream.Length];
        stream.ReadExactly(result);
        return result;
    }
}
