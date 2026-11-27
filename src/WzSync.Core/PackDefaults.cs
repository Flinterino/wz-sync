using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace WzSync.Core;

public sealed class PackDefaults
{
    private const string Version = "pack-defaults.1";
    private readonly Dictionary<string, byte[]> files = new();

    public PackDefaults()
    {
        using var resource = typeof(PackDefaults).Assembly.GetManifestResourceStream("WzSync.Core.pack-defaults.zip")
            ?? throw new InvalidDataException("Pack defaults are missing.");
        using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("config/", StringComparison.Ordinal) || entry.Length is <= 0 or > 1048576)
                throw new InvalidDataException("Invalid bundled default file.");
            ManagedPolicy.ValidateFile(entry.FullName);
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            files.Add(entry.FullName, output.ToArray());
        }
    }

    public SyncManifest Manifest(PackChoice pack)
    {
        var rows = files.Select(item =>
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(item.Value));
            var uri = new Uri($"https://github.com/Flinterino/wz-sync/releases/download/{Version}/{hash}{Path.GetExtension(item.Key)}");
            return new ManifestFile(item.Key, hash, item.Value.LongLength, "github", uri, null, "download", null);
        });
        return new SyncManifest(pack == PackChoice.Lite ? "lite" : "full", Version, files.Keys, rows);
    }

    public async Task ApplyAsync(ProfilePaths profile, SyncPlan plan, CancellationToken token,
        IProgress<SyncProgress>? progress)
    {
        if (plan.Manifest.Version != Version)
            throw new InvalidDataException("Review the bundled pack defaults first.");
        var payloads = plan.Manifest.Files.GroupBy(row => row.Url!).ToDictionary(group => group.Key, group => files[group.First().Path]);
        using var downloader = new VerifiedDownloader(new EmbeddedPayloads(payloads));
        await new TransactionEngine(profile, downloader).ApplyAsync(plan, token, progress);
    }

    private sealed class EmbeddedPayloads(IReadOnlyDictionary<Uri, byte[]> payloads) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(payloads.TryGetValue(request.RequestUri!, out var data)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
