using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WzSync.Core;

namespace WzStandalone;

public static class OfficialLauncherProfile
{
    public const string ProfileId = "wars-and-zombies-lite";
    public const string ProfileName = "Wars & Zombies Lite (WzSync)";

    public const int MaximumHeapGiB = 10;
    public static int RecommendedHeapGiB => RecommendHeap(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    internal static int RecommendHeap(long bytes)
    {
        const long gib = 1024L * 1024 * 1024;
        return bytes <= 0 ? 4 : (int)Math.Clamp(bytes / gib * 2 / 3, 2, MaximumHeapGiB);
    }

    internal static string WithHeapLimit(string? args, int gib)
    {
        if (gib is < 2 or > MaximumHeapGiB)
            throw new ArgumentOutOfRangeException(nameof(gib));

        // Remove top-level -Xmx tokens while preserving quoted Java argument values.
        var original = args ?? "";
        var flag = $"-Xmx{gib}G";
        const string argument = """(?:[^\s"]|"[^"]*")+""";
        var matches = Regex.Matches(original, argument);
        var retained = matches.Cast<Match>().Select(match => match.Value)
            .Where(value => !Regex.IsMatch(value.Trim('"'), @"^-Xmx\d+[kKmMgG]?$"));
        return string.Join(" ", retained.Append(flag));
    }

    public static string? Register(string launcherRoot, string instanceRoot, int? heapGiB = null)
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
        var originalArgs = profile["javaArgs"]?.GetValue<string>();
        var updatedArgs = WithHeapLimit(originalArgs, heapGiB ?? RecommendedHeapGiB);
        if (profile["lastVersionId"]?.GetValue<string>() == LiteInstance.LoaderVersion && originalArgs == updatedArgs)
            return null;
        profile["lastVersionId"] = LiteInstance.LoaderVersion;
        profile["javaArgs"] = updatedArgs;

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
