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
    [InlineData(".wzsync/a")]
    [InlineData("mods\\a.jar")]
    [InlineData("Mods/a.jar")]
    public void Unsafe_or_unapproved_paths_are_rejected(string path) => Assert.Throws<InvalidDataException>(() => ManagedPolicy.ValidateFile(path));

    [Theory]
    [InlineData("mods/a.jar")]
    [InlineData("config/DistantHorizons.toml")]
    public void Approved_files_are_accepted(string path) => ManagedPolicy.ValidateFile(path);

    [Fact]
    public void Exact_policy_has_nineteen_rules() => Assert.Equal(19, ManagedPolicy.Rules.Count);
}
