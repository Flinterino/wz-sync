using WzSync.Core;

namespace WzSync.Core.Tests;

public sealed class PolicyTests
{
    [Theory]
    [InlineData("../mods/a.jar")]
    [InlineData("mods/../a.jar")]
    [InlineData("C:/mods/a.jar")]
    [InlineData("mods/a.jar:stream")]
    [InlineData("mods/CON.jar")]
    [InlineData("mods/a. ")]
    [InlineData("mods/sub/a.jar")]
    [InlineData("config/options.txt")]
    [InlineData("options.txt")]
    [InlineData("config/defaultoptions/servers.dat")]
    [InlineData(".wzsync/a")]
    [InlineData("mods\\a.jar")]
    [InlineData("Mods/a.jar")]
    public void Unsafe_or_unapproved_paths_are_rejected(string path) => Assert.Throws<InvalidDataException>(() => ManagedPolicy.ValidateFile(path));

    [Theory]
    [InlineData("mods/a.jar")]
    [InlineData("config/DistantHorizons.toml")]
    [InlineData("config/defaultoptions/options.txt")]
    [InlineData("config/defaultoptions/keybindings.txt")]
    public void Approved_files_are_accepted(string path) => ManagedPolicy.ValidateFile(path);

    [Theory]
    [InlineData("mods/CON .jar")]
    [InlineData("mods/cOn   .jar")]
    [InlineData("mods/PRN .zip")]
    [InlineData("mods/nUl  .jar")]
    [InlineData("mods/COM1 .jar")]
    [InlineData("mods/lPt9  .zip")]
    [InlineData("mods/CONIN$")]
    [InlineData("mods/conout$")]
    [InlineData("mods/cOnIn$.jar")]
    [InlineData("mods/CONOUT$ .zip")]
    public void Windows_device_aliases_are_rejected(string path) => Assert.Throws<InvalidDataException>(() => ManagedPolicy.ValidateFile(path));
    [Fact]
    public void Spice_config_is_allowed_in_a_narrow_manifest_without_allowing_neighbors()
    {
        const string path = "config/spice_of_life_berry/client.toml";
        var manifest = ManifestParser.Parse(ManifestTests.Json(ManifestTests.Row(path), System.Text.Json.JsonSerializer.Serialize(path)));
        Assert.Equal(path, Assert.Single(manifest.Files).Path);
        Assert.Contains(path, ManagedPolicy.Rules);
        Assert.Throws<InvalidDataException>(() => ManagedPolicy.ValidateFile("config/spice_of_life_berry/common.toml"));
        Assert.Throws<InvalidDataException>(() => ManifestParser.Parse(ManifestTests.Json("", System.Text.Json.JsonSerializer.Serialize("config/spice_of_life_berry/"))));
    }

    [Fact]
    public void Exact_policy_has_twenty_two_rules() => Assert.Equal(22, ManagedPolicy.Rules.Count);
}
