using System.Net;
using System.Security.Cryptography;
namespace WzSync.Core;

public sealed class VerifiedDownloader : IDisposable
{
    private readonly HttpClient client;
    public VerifiedDownloader() : this(new HttpClientHandler { AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,UseDefaultCredentials=false,Credentials=null }) { }
    internal VerifiedDownloader(HttpMessageHandler handler) => client=new HttpClient(handler,true);
    public void Dispose() => client.Dispose();

    // stateRelativePath is relative to .wzsync and must be a new file beneath tmp/.
    // Failure leaves this operation's partial file in place; the caller owns guarded cleanup.
    public async Task<string> StageAsync(ProfilePaths paths,ManifestFile file,string expectedVersion,string stateRelativePath,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(paths); ArgumentNullException.ThrowIfNull(file);
        var uri=UrlPolicy.ValidateInitial(file,expectedVersion);
        if(!stateRelativePath.StartsWith("tmp/",StringComparison.Ordinal)) throw new InvalidDataException("Staging must be below state tmp.");
        paths.ResolveState(stateRelativePath);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            for(var redirects=0;;redirects++)
            {
                UrlPolicy.ValidateHop(uri);
                using var request=new HttpRequestMessage(HttpMethod.Get,uri);
                using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellationToken).ConfigureAwait(false);
                if(response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if(redirects>=5 || response.Headers.Location==null || !Uri.TryCreate(uri,response.Headers.Location,out var next)) throw new InvalidDataException("Invalid download redirect.");
                    UrlPolicy.ValidateHop(next); uri=next; continue;
                }
                if(response.StatusCode!=HttpStatusCode.OK) throw new InvalidDataException("Download returned an unavailable status.");
                if(response.Content.Headers.ContentLength is long length && length!=file.Size) throw new InvalidDataException("Download size mismatch.");
                // Guard each directory before and after creating it, including newly introduced entries.
                var segments=stateRelativePath.Split('/');
                var parent="";
                for(var i=0;i<segments.Length-1;i++)
                {
                    parent=parent.Length==0?segments[i]:parent+"/"+segments[i];
                    var directory=paths.ResolveState(parent);
                    Directory.CreateDirectory(directory);
                    paths.ResolveState(parent);
                }
                var stage=paths.ResolveState(stateRelativePath);
                await using(var output=new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous))
                {
                    await using var input=await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    paths.ResolveState(stateRelativePath);
                    using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer=new byte[81920]; long total=0;
                    while(true)
                    {
                        var count=await input.ReadAsync(buffer,cancellationToken).ConfigureAwait(false);
                        paths.ResolveState(stateRelativePath);
                        if(count==0) break;
                        if(count>file.Size-total) throw new InvalidDataException("Download exceeds expected size.");
                        hash.AppendData(buffer,0,count);
                        await output.WriteAsync(buffer.AsMemory(0,count),cancellationToken).ConfigureAwait(false);
                        paths.ResolveState(stateRelativePath); total+=count;
                    }
                    if(total!=file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Download integrity mismatch.");
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                stage=paths.ResolveState(stateRelativePath);
                if(!FileSnapshot.Capture(stage).Matches(file)) throw new InvalidDataException("Staged integrity mismatch.");
                paths.ResolveState(stateRelativePath);
                return stage;
            }
        }
        catch(HttpRequestException) { throw new IOException("Download request failed."); }
        catch(IOException) { throw new IOException("Download or staging I/O failed."); }
        catch(OperationCanceledException) { throw new OperationCanceledException("Download canceled.",cancellationToken); }
    }
}

