using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Security.Cryptography;
using WzSync.Core;

namespace WzStandalone;

public static class OfficialNeoForgeInstaller
{
    private const string InstallerName = "neoforge-21.1.265-installer.jar";
    private const string InstallerSha256 = "a547ccc30a80a675d1c61d04b55d10c45c39a86971e09731482c50a24399d1a2";
    private const int InstallerSize = 6_992_412;
    private static readonly Uri InstallerUri = new(
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.265/" + InstallerName);

    public static async Task<Process> StartAsync(
        string javaExe, string cacheRoot, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(javaExe) ||
            !(string.Equals(Path.GetFileName(javaExe), "java.exe", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(Path.GetFileName(javaExe), "javaw.exe", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Provide the full path to Java 21's java.exe or javaw.exe.", nameof(javaExe));
        }

        javaExe = Path.GetFullPath(javaExe);
        _ = new ProfilePaths(Path.GetDirectoryName(javaExe)!);
        CheckEntry(javaExe);
        if (!File.Exists(javaExe))
            throw new FileNotFoundException("The selected Java executable could not be found.");

        var appRoot = new ProfilePaths(cacheRoot);
        var installerFolder = Path.Combine(appRoot.Root, "installers");
        CheckEntry(installerFolder);
        Directory.CreateDirectory(installerFolder);
        var cache = new ProfilePaths(installerFolder);
        var installer = Path.Combine(cache.Root, InstallerName);
        CheckEntry(installer);
        if (!File.Exists(installer))
            await DownloadAsync(cache, installer, cancellationToken);

        cache.CheckRoot();
        CheckEntry(installer);
        await using var archive = OpenForVerification(installer);
        await VerifyInstallerAsync(archive, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var start = new ProcessStartInfo(javaExe)
        {
            UseShellExecute = false,
            WorkingDirectory = cache.Root
        };
        start.ArgumentList.Add("-jar");
        start.ArgumentList.Add(installer);
        foreach (var option in new[] { "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH" })
            start.Environment.Remove(option);

        return Process.Start(start) ?? throw new IOException("The NeoForge installer could not be started.");
    }

    public static async Task VerifyInstallerAsync(Stream installer, CancellationToken cancellationToken = default)
    {
        if (!installer.CanSeek || installer.Position != 0 || installer.Length != InstallerSize)
            throw new InvalidDataException("The NeoForge installer size does not match the pinned release.");
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(installer, cancellationToken));
        if (!string.Equals(digest, InstallerSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The NeoForge installer SHA-256 does not match the pinned release.");
    }

    private static async Task DownloadAsync(ProfilePaths cache, string installer, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var temporary = Path.Combine(cache.Root, Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            using var response = await client.GetAsync(InstallerUri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                throw new IOException("NeoForge's Maven host did not return the pinned installer.");
            if (response.Content.Headers.ContentLength is long length && length != InstallerSize)
                throw new InvalidDataException("The NeoForge installer response size is unexpected.");

            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
            cache.CheckRoot();
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous))
            {
                created = true;
                await CopyBoundedAsync(source, output, deadline.Token);
            }

            await using (var verified = OpenForVerification(temporary))
                await VerifyInstallerAsync(verified, deadline.Token);

            cache.CheckRoot();
            File.Move(temporary, installer);
            created = false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Downloading the pinned NeoForge installer timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new IOException("Could not download the pinned installer from NeoForge.", ex);
        }
        finally
        {
            if (created)
            {
                cache.CheckRoot();
                File.Delete(temporary);
            }
        }
    }

    private static async Task CopyBoundedAsync(Stream source, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = InstallerSize;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), cancellationToken);
            if (read == 0)
                break;
            if (read > remaining)
                throw new InvalidDataException("The NeoForge installer response exceeds the pinned file size.");

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static FileStream OpenForVerification(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);

    private static void CheckEntry(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The installer and Java paths may not use reparse points.");
        }
        catch (FileNotFoundException) { }
    }
}
