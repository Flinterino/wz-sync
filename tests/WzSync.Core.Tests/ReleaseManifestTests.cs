using System.Net;
using System.Security.Cryptography;
using System.Text;
using WzSync.Core;

namespace WzSync.Core.Tests;

public sealed class ReleaseManifestTests
{
    private const string Releases = "https://github.com/Flinterino/wz-sync/releases/";
    private const string Full = "manifest-full.json";

    [Fact]
    public async Task Pins_both_files_to_the_same_release_tag()
    {
        var json = TestManifest();
        using var handler = new Handler(uri => uri.AbsoluteUri switch
        {
            Releases + "latest/download/" + Full => Redirect("/Flinterino/wz-sync/releases/download/v1/" + Full),
            Releases + "download/v1/" + Full => Redirect("https://release-assets.githubusercontent.com/example?sig=fixture"),
            "https://release-assets.githubusercontent.com/example?sig=fixture" => Body(json),
            Releases + "download/v1/" + Full + ".sha256" => Redirect("https://objects.githubusercontent.com/example"),
            "https://objects.githubusercontent.com/example" => Body(Sidecar(json, Full)),
            _ => throw new InvalidOperationException("Unexpected request.")
        });
        using var client = new ReleaseManifestClient(handler);
        var manifest = await client.FetchAsync(PackChoice.Full);
        Assert.Equal("full", manifest.Pack);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Single(handler.Requests, uri => uri.AbsolutePath.Contains("/latest/"));
    }

    [Theory]
    [InlineData("https://github.com/other/project/releases/download/v1/manifest-full.json")]
    [InlineData("https://github.com/Flinterino/wz-sync/releases/download/v1/other.json")]
    [InlineData("https://release-assets.githubusercontent.com/asset")]
    [InlineData("https://cdn.modrinth.com/manifest-full.json")]
    [InlineData("http://github.com/Flinterino/wz-sync/releases/download/v1/manifest-full.json")]
    public async Task Refuses_unapproved_targets_before_requesting_them(string target)
    {
        using var handler = new Handler(_ => Redirect(target));
        using var client = new ReleaseManifestClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.FetchAsync(PackChoice.Full));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Refuses_checksum_redirect_to_a_different_tag()
    {
        using var handler = new Handler(uri => uri.AbsoluteUri switch
        {
            Releases + "latest/download/" + Full => Redirect(Releases + "download/v1/" + Full),
            Releases + "download/v1/" + Full => Body(TestManifest()),
            Releases + "download/v1/" + Full + ".sha256" => Redirect(Releases + "download/v2/" + Full + ".sha256"),
            _ => throw new InvalidOperationException("Unexpected request.")
        });
        using var client = new ReleaseManifestClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.FetchAsync(PackChoice.Full));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("name")]
    [InlineData("pack")]
    [InlineData("length")]
    [InlineData("unpublished")]
    [InlineData("source")]
    [InlineData("version")]
    public async Task Rejects_unverified_manifest(string fault)
    {
        const string asset = "manifest-lite.json";
        var rows = fault == "source"
            ? """{"path":"mods/fixture.jar","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1,"source":"modrinth","url":"https://unapproved.invalid/fixture.jar","page":null,"availability":"download","reason":null}"""
            : "";
        var body = fault == "pack" ? TestManifest() : TestManifest(rows).Replace("\"full\"", "\"lite\"");
        if (fault == "version") body = body.Replace("\"v1\"", "\"v2\"");
        using var handler = new Handler(uri =>
        {
            if (uri.AbsolutePath.Contains("/latest/")) return Redirect(Releases + "download/v1/" + asset);
            if (uri.AbsolutePath.EndsWith(".sha256"))
                return Body(Sidecar(fault == "digest" ? "{}" : body, fault == "name" ? Full : asset));
            if (fault == "unpublished") return new HttpResponseMessage(HttpStatusCode.NotFound);
            var response = Body(body);
            if (fault == "length") response.Content.Headers.ContentLength = ManifestParser.MaximumBytes + 1;
            return response;
        });
        using var client = new ReleaseManifestClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.FetchAsync(PackChoice.Lite));
    }

    private static string TestManifest(string rows = "") => ManifestTests.Json(rows).Replace("2026.11.18", "v1");

    private static string Sidecar(string json, string asset) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))) + "  " + asset + "\n";

    private static HttpResponseMessage Body(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };

    private static HttpResponseMessage Redirect(string url)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(url, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            return Task.FromResult(respond(uri));
        }
    }
}
