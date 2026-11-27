using System.IO;
using System.Text.Json;
using WzStandalone;

namespace WzSync.App.Tests;

public sealed class StandaloneMemoryTests
{
    [Theory]
    [InlineData(4, 2)]
    [InlineData(8, 5)]
    [InlineData(16, 10)]
    [InlineData(32, 10)]
    public void Heap_is_adaptive_and_never_above_ten_gib(int physicalGiB, int expected)
    {
        Assert.Equal(expected, OfficialLauncherProfile.RecommendHeap(physicalGiB * 1024L * 1024 * 1024));
    }

    [Fact]
    public void Profile_registration_replaces_two_gib_heap_and_preserves_other_profiles_and_java_flags()
    {
        var root = Directory.CreateTempSubdirectory("wz-memory-");
        try
        {
            var launcher = root.CreateSubdirectory("launcher").FullName;
            var game = root.CreateSubdirectory("instance").FullName;
            var version = Directory.CreateDirectory(Path.Combine(launcher, "versions", LiteInstance.LoaderVersion)).FullName;
            File.WriteAllText(Path.Combine(version, LiteInstance.LoaderVersion + ".json"),
                JsonSerializer.Serialize(new { id = LiteInstance.LoaderVersion, inheritsFrom = LiteInstance.MinecraftVersion }));
            var profiles = Path.Combine(launcher, "launcher_profiles.json");
            var original = JsonSerializer.Serialize(new { profiles = new Dictionary<string, object>
            {
                [OfficialLauncherProfile.ProfileId] = new
                {
                    name = OfficialLauncherProfile.ProfileName, gameDir = game, lastVersionId = LiteInstance.LoaderVersion,
                    javaArgs = """-Xmx2G -Dmessage="keep -Xmx3G inside this quote" -Xmx2048m -XX:+UseG1GC"""
                },
                ["other"] = new { name = "Other installation", javaArgs = "-Xmx3G" }
            }});
            File.WriteAllText(profiles, original);
            var backup = Assert.IsType<string>(OfficialLauncherProfile.Register(launcher, game, 10));
            Assert.Equal(original, File.ReadAllText(backup));
            using var updated = JsonDocument.Parse(File.ReadAllBytes(profiles));
            var list = updated.RootElement.GetProperty("profiles");
            Assert.Equal("""-Dmessage="keep -Xmx3G inside this quote" -XX:+UseG1GC -Xmx10G""",
                list.GetProperty(OfficialLauncherProfile.ProfileId).GetProperty("javaArgs").GetString());
            Assert.Equal("-Xmx3G", list.GetProperty("other").GetProperty("javaArgs").GetString());
            Assert.Null(OfficialLauncherProfile.Register(launcher, game, 10));
        }
        finally
        {
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Assert.Equal(parent, Path.GetDirectoryName(root.FullName));
            root.Delete(recursive: true);
        }
    }
}
