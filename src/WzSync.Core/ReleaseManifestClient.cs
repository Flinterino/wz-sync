using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WzSync.Core;

public enum PackChoice { Full, Lite }

public sealed class ReleaseManifestClient : IDisposable
{
    public const int MaximumChecksumBytes = 256;
    private const string ReleasePath = "/Flinterino/wz-sync/releases/";
    private const string Origin = "https://github.com";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex TagName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant);
    private readonly HttpClient client;

    public ReleaseManifestClient() : this(UrlPolicy.CreateHandler()) { }

    internal ReleaseManifestClient(HttpMessageHandler handler) => client = new HttpClient(handler);

    public async Task<SyncManifest> FetchAsync(PackChoice pack, CancellationToken cancellationToken = default)
    {
        var name = ManifestName(pack);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var (manifestBytes, tag) = await ReadAssetAsync(name, null, ManifestParser.MaximumBytes, deadline.Token).ConfigureAwait(false);
            var (checksumBytes, _) = await ReadAssetAsync(name + ".sha256", tag, MaximumChecksumBytes, deadline.Token).ConfigureAwait(false);
            return VerifyManifest(manifestBytes, checksumBytes, pack, tag);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The release request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new IOException("Could not reach GitHub to check for updates.");
        }
    }

    public static SyncManifest VerifyManifest(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> checksumBytes,
        PackChoice pack, string tag)
    {
        var name = ManifestName(pack);
        if (!IsReleaseTag(tag) || manifestBytes.Length > ManifestParser.MaximumBytes || checksumBytes.Length > MaximumChecksumBytes)
            throw new InvalidDataException("Invalid release tag or manifest file size.");
        try
        {
            var expected = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) + "  " + name;
            var checksum = StrictUtf8.GetString(checksumBytes);
            if (checksum != expected && checksum != expected + "\n" && checksum != expected + "\r\n")
                throw new InvalidDataException("The manifest SHA-256 does not match its release checksum.");
            var manifest = ManifestParser.Parse(StrictUtf8.GetString(manifestBytes));
            if (manifest.Pack != (pack == PackChoice.Full ? "full" : "lite"))
                throw new InvalidDataException("The release manifest does not match the selected pack.");
            if (manifest.Version != tag)
                throw new InvalidDataException("The manifest version does not match its release tag.");
            foreach (var file in manifest.Files)
                if (file.Availability == "download") UrlPolicy.ValidateInitial(file, manifest.Version);
            return manifest;
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("The release files are not valid UTF-8.");
        }
    }

    private static string ManifestName(PackChoice pack) => pack switch
    {
        PackChoice.Full => "manifest-full.json",
        PackChoice.Lite => "manifest-lite.json",
        _ => throw new ArgumentOutOfRangeException(nameof(pack))
    };

    private static bool IsReleaseTag(string tag) => TagName.IsMatch(tag) && !tag.EndsWith('.');

    private async Task<(byte[] Bytes, string Tag)> ReadAssetAsync(string name, string? tag, int maximumBytes,
        CancellationToken token)
    {
        var route = tag == null ? "latest/download/" : $"download/{tag}/";
        var uri = new Uri(Origin + ReleasePath + route + name);
        for (var redirects = 0; ; redirects++)
        {
            tag = ValidateAsset(uri, name, tag);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (UrlPolicy.IsRedirect(response.StatusCode))
            {
                uri = UrlPolicy.ResolveRedirect(uri, response.Headers.Location, redirects);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidDataException("The selected pack files are missing from the latest GitHub release.");
            if (response.StatusCode != HttpStatusCode.OK || tag == null)
                throw new InvalidDataException("GitHub did not return a tagged release asset.");

            var expectedLength = response.Content.Headers.ContentLength;
            if (expectedLength > maximumBytes) throw new InvalidDataException("The release file exceeds its size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) break;
                if (count > maximumBytes - bytes.Length)
                    throw new InvalidDataException("The release file exceeds its size limit.");
                bytes.Write(buffer, 0, count);
            }
            if (expectedLength.HasValue && expectedLength != bytes.Length)
                throw new InvalidDataException("The release file is incomplete.");
            return (bytes.ToArray(), tag);
        }
    }

    private static string? ValidateAsset(Uri uri, string name, string? tag)
    {
        UrlPolicy.ValidateHttps(uri);
        if (uri.Host == "github.com" && uri.Query.Length == 0)
        {
            if (tag == null && uri.AbsolutePath == ReleasePath + "latest/download/" + name)
                return null;
            var prefix = ReleasePath + "download/";
            if (uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                var segments = uri.AbsolutePath[prefix.Length..].Split('/');
                if (segments.Length == 2 && segments[1] == name && IsReleaseTag(segments[0]) && (tag == null || tag == segments[0]))
                    return segments[0];
            }
        }
        if (tag != null && UrlPolicy.IsGitHubAssetHost(uri.Host))
            return tag;
        throw new InvalidDataException("A release request left the approved repository or asset route.");
    }

    public void Dispose() => client.Dispose();
}
