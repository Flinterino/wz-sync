using System.Security.Cryptography;
using System.Text;
using WzSync.Core;
using WzSync.Release;

namespace WzSync.Core.Tests;

public sealed class ReleaseBundleTests : IDisposable
{
    private const string Tag = "2026.11.18";
    private static readonly byte[] OwnedBytes = Encoding.UTF8.GetBytes("fixture-owned-config");
    private static readonly string OwnedHash = Convert.ToHexStringLower(SHA256.HashData(OwnedBytes));
    private static readonly string OwnedName = OwnedHash + ".json";
    private readonly string root = Directory.CreateTempSubdirectory("wz-sync-release-fixture-").FullName;

    [Fact]
    public void Accepts_both_manifests_with_one_shared_owned_file_and_no_third_party_payload()
    {
        Fixture();
        Assert.Equal(1, ReleaseBundle.Validate(root, Tag));
        Assert.Equal(5, Directory.GetFileSystemEntries(root).Length);
    }

    [Theory]
    [InlineData("mods/wz_fixture.jar", true)]
    [InlineData("mods/external-fixture.jar", false)]
    public void Github_JAR_rows_follow_the_owned_path_contract(string path, bool accepted)
    {
        var name = OwnedHash + ".jar";
        var owned = $$"""{"path":"{{path}}","sha256":"{{OwnedHash}}","size":{{OwnedBytes.Length}},"source":"github","url":"https://github.com/Flinterino/wz-sync/releases/download/{{Tag}}/{{name}}","page":null,"availability":"download","reason":null}""";
        var full = ManifestTests.Json(owned);
        WriteManifest("full", full);
        WriteManifest("lite", full.Replace("\"full\"", "\"lite\""));
        File.WriteAllBytes(Path.Combine(root, name), OwnedBytes);
        if (accepted) Assert.Equal(1, ReleaseBundle.Validate(root, Tag));
        else Assert.Throws<InvalidDataException>(() => ReleaseBundle.Validate(root, Tag));
    }

    [Theory]
    [InlineData("mods/opacnationsandwarfare-0.2.5-test.jar", "30633a2b43e56037fceb5ed8ee787277177dd916d9544da69655c6182e7207a5", true)]
    [InlineData("mods/seasonalfoliage-0.8.3.jar", "fd5d1f82b000959e4c1d641e17ad1eb566fb33b7c4c4f584b256bf7b6625a9dc", true)]
    [InlineData("mods/seasonalfoliage-0.9.1.jar", "6078287642b85c122fe247be784e0bf4e57f43178a936b01a0ebb3c1f6a3b39f", true)]
    [InlineData("mods/opacnationsandwarfare-0.2.5-test.jar", "wrong-hash", false)]
    [InlineData("mods/opacnationsandwarfare-0.2.6-test.jar", "30633a2b43e56037fceb5ed8ee787277177dd916d9544da69655c6182e7207a5", false)]
    public void Non_Wz_project_jars_require_the_approved_path_and_hash(string path, string hash, bool allowed)
    {
        Assert.Equal(allowed, ReleaseBundle.IsApprovedProjectJar(path, hash));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("sidecar")]
    [InlineData("version")]
    [InlineData("pack")]
    [InlineData("path")]
    [InlineData("url")]
    [InlineData("directory")]
    [InlineData("empty")]
    public void Rejects_unsafe_or_unmatched_bundle(string fault)
    {
        Fixture();
        switch (fault)
        {
            case "missing": File.Delete(Path.Combine(root, OwnedName)); break;
            case "extra": File.WriteAllText(Path.Combine(root, "unexpected.jar"), "fixture"); break;
            case "size": File.AppendAllText(Path.Combine(root, OwnedName), "more"); break;
            case "hash": File.WriteAllBytes(Path.Combine(root, OwnedName), new byte[OwnedBytes.Length]); break;
            case "sidecar": File.AppendAllText(Path.Combine(root, "manifest-full.json.sha256"), "bad"); break;
            case "version": ChangeLite(Tag, "v-wrong"); break;
            case "pack": ChangeLite("\"lite\"", "\"full\""); break;
            case "path": ChangeLite("config/sodium-options.json", "mods/../other.json"); break;
            case "url": ChangeLite("/Flinterino/wz-sync/", "/Other/project/"); break;
            case "directory": Directory.CreateDirectory(Path.Combine(root, "unexpected")); break;
            case "empty": WriteManifest("lite", ManifestTests.Json().Replace("\"full\"", "\"lite\"")); break;
        }
        Assert.Throws<InvalidDataException>(() => ReleaseBundle.Validate(root, Tag));
    }

    [Fact]
    public void Rejects_linked_payload_when_symlinks_are_supported()
    {
        Fixture();
        var asset = Path.Combine(root, OwnedName);
        File.Delete(asset);
        try { File.CreateSymbolicLink(asset, Path.Combine(root, "unavailable")); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; }
        var result = Assert.Throws<InvalidDataException>(() => ReleaseBundle.Validate(root, Tag));
        Assert.Contains("regular files", result.Message);
    }

    private void Fixture()
    {
        // Test-only bytes. Production manifests and owned payloads come from the upstream pack builder.
        var owned = $$"""{"path":"config/sodium-options.json","sha256":"{{OwnedHash}}","size":{{OwnedBytes.Length}},"source":"github","url":"https://github.com/Flinterino/wz-sync/releases/download/{{Tag}}/{{OwnedName}}","page":null,"availability":"download","reason":null}""";
        var external = $$"""{"path":"mods/fixture-cdn.jar","sha256":"{{ManifestTests.Hash}}","size":100,"source":"modrinth","url":"https://cdn.modrinth.com/example","page":null,"availability":"download","reason":null}""";
        var full = ManifestTests.Json(owned + "," + external, "\"mods/\",\"config/sodium-options.json\"");
        WriteManifest("full", full);
        WriteManifest("lite", full.Replace("\"full\"", "\"lite\""));
        File.WriteAllBytes(Path.Combine(root, OwnedName), OwnedBytes);
    }

    private void ChangeLite(string before, string after) =>
        WriteManifest("lite", File.ReadAllText(Path.Combine(root, "manifest-lite.json")).Replace(before, after));

    private void WriteManifest(string pack, string json)
    {
        var name = $"manifest-{pack}.json";
        var bytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(Path.Combine(root, name), bytes);
        File.WriteAllText(Path.Combine(root, name + ".sha256"),
            Convert.ToHexStringLower(SHA256.HashData(bytes)) + "  " + name + "\n");
    }

    public void Dispose() => Directory.Delete(root, true);
}
