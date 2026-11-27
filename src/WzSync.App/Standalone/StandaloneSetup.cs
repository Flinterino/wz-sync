using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WzStandalone;
using WzSync.Core;

namespace WzSync.App;

internal static class StandaloneSetup
{
    internal static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    internal static string Instance => LiteInstance.GetPath(AppData);
    internal static string OfficialRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    internal static bool HasLoader => File.Exists(Path.Combine(OfficialRoot, "versions", LiteInstance.LoaderVersion,
        LiteInstance.LoaderVersion + ".json"));

    internal static ProfileCandidate? Profile
    {
        get
        {
            if (!Directory.Exists(Instance)) return null;
            try
            {
                return new ProfileCandidate("Wars & Zombies Lite (Standalone)", Open(Instance).Root, IsStandalone: true);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    internal static ProfilePaths Open(string directory)
    {
        var paths = new ProfilePaths(directory);
        if (!string.Equals(paths.Root, Instance, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select the Lite installation created by this launcher.");
        var marker = Path.Combine(paths.Root, "instance.json");
        if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked standalone profile markers are forbidden.");
        using var stream = File.OpenRead(marker);
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        if (!root.TryGetProperty("imported", out var imported) || imported.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("loader", out var loader) || loader.GetString() != LiteInstance.LoaderVersion)
            throw new InvalidDataException("Import the verified Lite files first.");
        return paths;
    }

    internal static void RequireLauncherClosed()
    {
        var running = Process.GetProcessesByName("MinecraftLauncher");
        try
        {
            if (running.Length != 0)
                throw new InvalidDataException("Close the official Minecraft Launcher before setup, then try again.");
        }
        finally
        {
            foreach (var process in running) process.Dispose();
        }
    }

    internal static void OpenOfficialLauncher()
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
        start.ArgumentList.Add(@"shell:AppsFolder\Microsoft.4297127D64EC6_8wekyb3d8bbwe!Minecraft");
        using (Process.Start(start)) { }
    }
}
