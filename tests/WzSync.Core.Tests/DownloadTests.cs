using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using WzSync.Core;
namespace WzSync.Core.Tests;
public sealed class DownloadTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"wzsync-download-"+Guid.NewGuid().ToString("N"));
    public DownloadTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root,true);
    private static readonly byte[] Bytes=Encoding.UTF8.GetBytes("payload");
    private static readonly string Hash=Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
    private static ManifestFile Row(string? url=null,string source="github",long? size=null,string? hash=null) => new("mods/a.jar",hash??Hash,size??Bytes.Length,source,new Uri(url??$"https://github.com/Flinterino/wz-sync/releases/download/v1/{Hash}.jar"),null,"download",null);
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; }=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        { Requests.Add(request.RequestUri!); return Task.FromResult(send(request)); }
    }
    private static HttpResponseMessage Body(byte[] bytes) => new(HttpStatusCode.OK) { Content=new ByteArrayContent(bytes) };
    private static HttpResponseMessage Redirect(string url,HttpStatusCode code=HttpStatusCode.Found)
    { var r=new HttpResponseMessage(code); r.Headers.TryAddWithoutValidation("Location",url); return r; }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length=0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream,TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
    [Theory] [InlineData(6)] [InlineData(8)] [InlineData(7)]
    public async Task Stream_without_length_is_bounded_and_hashed(int count)
    {
        using var handler=new Handler(_=>new HttpResponseMessage(HttpStatusCode.OK) { Content=new UnknownLengthContent(new byte[count]) }); using var downloader=new VerifiedDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a"));
        var stage=Path.Combine(root,".wzsync/tmp/a"); Assert.True(new FileInfo(stage).Length<=Bytes.Length);
    }
    [Theory] [InlineData("modrinth","cdn.modrinth.com")] [InlineData("curseforge","edge.forgecdn.net")] [InlineData("curseforge","mediafilez.forgecdn.net")]
    public async Task Allowed_source_origins_work(string source,string host)
    {
        using var handler=new Handler(_=>Body(Bytes)); using var downloader=new VerifiedDownloader(handler);
        var stage=await downloader.StageAsync(new ProfilePaths(root),Row("https://"+host+"/asset",source),"v1","tmp/a"); Assert.Equal(Bytes,File.ReadAllBytes(stage));
    }
    [Theory] [InlineData("v2")] [InlineData("v1/other")] [InlineData("../v1")]
    public async Task Github_version_identity_rejected(string version)
    {
        using var handler=new Handler(_=>Body(Bytes)); using var downloader=new VerifiedDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),version,"tmp/a")); Assert.Empty(handler.Requests);
    }
    [Fact] public async Task Canceled_stage_does_not_request_or_write()
    {
        using var handler=new Handler(_=>Body(Bytes)); using var downloader=new VerifiedDownloader(handler);
        using var canceled=new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a",canceled.Token)); Assert.Empty(handler.Requests); Assert.Empty(Directory.GetFileSystemEntries(root));
    }
    [Fact] public async Task Symlink_stage_parent_rejected()
    {
        Directory.CreateDirectory(Path.Combine(root,".wzsync")); Directory.CreateDirectory(Path.Combine(root,"outside"));
        var link=Path.Combine(root,".wzsync/tmp");
        try { Directory.CreateSymbolicLink(link,Path.Combine(root,"outside")); }
        catch(UnauthorizedAccessException) when(OperatingSystem.IsWindows()) { return; }
        try {
            using var handler=new Handler(_=>Body(Bytes)); using var downloader=new VerifiedDownloader(handler);
            await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a")); Assert.Empty(handler.Requests);
        } finally { Directory.Delete(link); }
    }
    [Fact] public async Task Signed_asset_redirect_is_verified_and_staged()
    {
        using var handler=new Handler(r=>r.RequestUri!.Host=="github.com"?Redirect("https://release-assets.githubusercontent.com/blob?signature=secret"):Body(Bytes));
        using var downloader=new VerifiedDownloader(handler);
        var path=await downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/operation/a.part");
        Assert.Equal(Bytes,File.ReadAllBytes(path)); Assert.Equal(2,handler.Requests.Count);
    }
    [Theory]
    [InlineData("https://github.com.evil/asset")]
    [InlineData("http://github.com/asset")]
    [InlineData("https://user@github.com/asset")]
    [InlineData("https://github.com:444/asset")]
    [InlineData("https://github.com/asset?secret=x")]
    [InlineData("https://cdn.modrinth.com/asset")]
    [InlineData("https://github.com/other/repo/releases/download/v1/file.jar")]
    public async Task Invalid_initial_url_never_requested(string url)
    {
        using var handler=new Handler(_=>Body(Bytes)); using var downloader=new VerifiedDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(url),"v1","tmp/a"));
        Assert.Empty(handler.Requests); Assert.Empty(Directory.GetFileSystemEntries(root));
    }
    [Theory]
    [InlineData("https://evil.test/asset")]
    [InlineData("http://cdn.modrinth.com/asset")]
    [InlineData("https://user@cdn.modrinth.com/asset")]
    [InlineData("https://cdn.modrinth.com:444/asset")]
    [InlineData("https://cdn.modrinth.com/asset#fragment")]
    public async Task Invalid_redirect_never_requested(string location)
    {
        using var handler=new Handler(_=>Redirect(location)); using var downloader=new VerifiedDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a")); Assert.Single(handler.Requests);
    }
    [Theory]
    [InlineData(" \t/blob")]
    [InlineData(" \r\n/blob")]
    [InlineData("/bl\tob")]
    [InlineData("/%09blob")]
    [InlineData("/bl\\ob")]
    public async Task Raw_redirect_controls_rejected_before_resolution(string location)
    {
        using var handler=new Handler(r=>r.RequestUri!.AbsolutePath=="/blob"?Body(Bytes):new HttpResponseMessage(HttpStatusCode.Found) { Headers={ Location=new Uri(location,UriKind.Relative) } });
        using var downloader=new VerifiedDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a")); Assert.Single(handler.Requests);
    }
    [Fact] public async Task Redirect_loop_bounded()
    {
        using var handler=new Handler(_=>Redirect("/loop")); using var downloader=new VerifiedDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a")); Assert.Equal(6,handler.Requests.Count);
    }
    [Theory] [InlineData(6)] [InlineData(8)] [InlineData(7)]
    public async Task Short_long_and_wrong_hash_rejected(int size)
    {
        using var handler=new Handler(_=>Body(new byte[size])); using var downloader=new VerifiedDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a"));
    }
    [Theory] [InlineData("../escape")] [InlineData("backup/a")] [InlineData("tmp/../../mods/a.jar")] [InlineData("tmp")]
    public async Task Staging_escape_rejected(string stage)
    {
        using var handler=new Handler(_=>Body(Bytes)); using var downloader=new VerifiedDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),"v1",stage)); Assert.Empty(handler.Requests);
    }
    [Fact] public async Task Existing_stage_not_overwritten()
    {
        var paths=new ProfilePaths(root); Directory.CreateDirectory(Path.Combine(root,".wzsync/tmp")); var stage=Path.Combine(root,".wzsync/tmp/a"); File.WriteAllText(stage,"original");
        using var handler=new Handler(_=>Body(Bytes)); using var downloader=new VerifiedDownloader(handler);
        await Assert.ThrowsAsync<IOException>(()=>downloader.StageAsync(paths,Row(),"v1","tmp/a")); Assert.Equal("original",File.ReadAllText(stage));
    }
    [Theory] [InlineData(300)] [InlineData(304)] [InlineData(404)] [InlineData(206)]
    public async Task Unsupported_status_rejected(int status)
    {
        using var handler=new Handler(_=>new HttpResponseMessage((HttpStatusCode)status)); using var downloader=new VerifiedDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a"));
    }
    [Fact] public async Task Relative_redirect_is_resolved()
    {
        using var handler=new Handler(r=>r.RequestUri!.AbsolutePath=="/blob"?Body(Bytes):Redirect("/blob")); using var downloader=new VerifiedDownloader(handler);
        await downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a"); Assert.Equal("https://github.com/blob",handler.Requests[1].AbsoluteUri);
    }
    [Fact] public async Task Exact_bytes_without_content_length_are_verified()
    {
        using var handler=new Handler(_=>new HttpResponseMessage(HttpStatusCode.OK) { Content=new UnknownLengthContent(Bytes) }); using var downloader=new VerifiedDownloader(handler);
        var stage=await downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a"); Assert.Equal(Bytes,File.ReadAllBytes(stage));
    }
    [Fact] public async Task Newly_introduced_junction_is_rejected_after_response()
    {
        if(!OperatingSystem.IsWindows()) return;
        var link=Path.Combine(root,".wzsync/tmp"); Directory.CreateDirectory(Path.Combine(root,"outside"));
        using var handler=new Handler(_=> {
            Directory.CreateDirectory(Path.Combine(root,".wzsync"));
            using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",$"/c mklink /J \"{link}\" \"{Path.Combine(root,"outside")}\"") { CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true }); process!.WaitForExit(); Assert.Equal(0,process.ExitCode); return Body(Bytes);
        }); using var downloader=new VerifiedDownloader(handler);
        try { await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a")); Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root,"outside"))); }
        finally { if(Directory.Exists(link)) Directory.Delete(link); }
    }
    [Fact] public async Task Missing_location_rejected()
    {
        using var handler=new Handler(_=>new HttpResponseMessage(HttpStatusCode.Found)); using var downloader=new VerifiedDownloader(handler);
        await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a"));
    }
    [Fact] public async Task Network_error_does_not_expose_signed_url()
    {
        using var handler=new Handler(_=>throw new HttpRequestException("https://release-assets.githubusercontent.com/?secret=x")); using var downloader=new VerifiedDownloader(handler);
        var error=await Assert.ThrowsAsync<IOException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a")); Assert.DoesNotContain("secret",error.ToString());
    }
    [Fact] public async Task Junction_stage_parent_rejected()
    {
        if(!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(root,".wzsync")); Directory.CreateDirectory(Path.Combine(root,"outside"));
        using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",$"/c mklink /J \"{Path.Combine(root,".wzsync/tmp")}\" \"{Path.Combine(root,"outside")}\"") { CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true }); process!.WaitForExit(); Assert.Equal(0,process.ExitCode);
        try {
            using var handler=new Handler(_=>Body(Bytes)); using var downloader=new VerifiedDownloader(handler);
            await Assert.ThrowsAsync<InvalidDataException>(()=>downloader.StageAsync(new ProfilePaths(root),Row(),"v1","tmp/a")); Assert.Empty(handler.Requests); Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root,"outside")));
        } finally { Directory.Delete(Path.Combine(root,".wzsync/tmp")); }
    }
}
