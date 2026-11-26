using WzSync.Core;
namespace WzSync.Core.Tests;
public sealed class ManifestTests
{
    internal const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal static string Json(string files = "", string managed = "\"mods/\"") => $$"""{"schema":1,"pack":"full","version":"2026.11.18","minecraft":"1.21.1","loader":"neoforge-21.1.265","managed":[{{managed}}],"files":[{{files}}]}""";
    internal static string Row(string path = "mods/a.jar", string availability = "manual") => $$"""{"path":"{{path}}","sha256":"{{Hash}}","size":1,"source":"unresolved","url":null,"page":null,"availability":"{{availability}}","reason":"Acquire manually"}""";
    [Fact] public void Valid_manifest_is_immutable_and_parsed() { var m=ManifestParser.Parse(Json(Row())); Assert.Single(m.Files); Assert.Equal("full",m.Pack); }
    [Fact] public void Duplicate_keys_rejected() => Assert.Throws<InvalidDataException>(()=>ManifestParser.Parse(Json().Replace("\"schema\":1", "\"schema\":1,\"schema\":1")));
    [Fact] public void Unknown_keys_rejected() => Assert.Throws<InvalidDataException>(()=>ManifestParser.Parse(Json().Replace("\"schema\":1", "\"schema\":1,\"other\":1")));
    [Fact] public void File_outside_narrowed_scope_rejected() => Assert.Throws<InvalidDataException>(()=>ManifestParser.Parse(Json(Row(),"\"mods/b.jar\"")));
    [Fact] public void Case_collisions_rejected() => Assert.Throws<InvalidDataException>(()=>ManifestParser.Parse(Json(Row()+","+Row("mods/A.jar"))));
    [Theory] [InlineData("\"size\":1","\"size\":-1")] [InlineData("\"availability\":\"manual\"","\"availability\":\"download\"")] [InlineData("\"source\":\"unresolved\"","\"source\":\"unknown\"")] [InlineData("\"reason\":\"Acquire manually\"","\"reason\":null")] [InlineData("\"sha256\":\"aaaa","\"sha256\":\"xxxx")]
    public void Invalid_rows_rejected(string before,string after) => Assert.Throws<InvalidDataException>(()=>ManifestParser.Parse(Json(Row()).Replace(before,after)));
    [Fact] public void Bound_input() => Assert.Throws<InvalidDataException>(()=>ManifestParser.Parse(new string(' ',ManifestParser.MaximumBytes+1)));
}
