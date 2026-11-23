using System.Net;

namespace WzSync.Core;
internal static class UrlPolicy
{
    internal static void ValidateHop(Uri uri,string source,Uri? initial=null)
    {
        ValidateHttps(uri);
        var allowed=source switch
        {
            "github" when uri.Host=="github.com" => initial==null || uri.AbsolutePath==initial.AbsolutePath,
            "github" => initial!=null && IsGitHubAssetHost(uri.Host),
            "curseforge" => uri.Host is "edge.forgecdn.net" or "mediafilez.forgecdn.net",
            "modrinth" => uri.Host=="cdn.modrinth.com",
            _ => false
        };
        if(!allowed) throw new InvalidDataException("Download URL does not match the manifest source.");
    }
    internal static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect=false, UseCookies=false, AutomaticDecompression=DecompressionMethods.None,
        UseDefaultCredentials=false, Credentials=null
    };

    internal static bool IsGitHubAssetHost(string host) =>
        host is "objects.githubusercontent.com" or "release-assets.githubusercontent.com";

    internal static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    internal static Uri ResolveRedirect(Uri previous,Uri? location,int redirects)
    {
        if(redirects>=5 || location==null) throw new InvalidDataException("Invalid download redirect.");
        ValidateText(location.OriginalString);
        if(!Uri.TryCreate(previous,location,out var next)) throw new InvalidDataException("Invalid download redirect.");
        return next;
    }

    internal static void ValidateHttps(Uri uri)
    {
        ValidateText(uri.OriginalString);
        if(!uri.IsAbsoluteUri || uri.Scheme!="https" || uri.Port!=443 || uri.UserInfo.Length!=0 || uri.Fragment.Length!=0)
            throw new InvalidDataException("Download URL is forbidden.");
    }

    internal static void ValidateText(string raw)
    {
        if(raw.Any(char.IsControl) || raw.Contains('\\')) throw new InvalidDataException("Download URL contains forbidden characters.");
        for(var i=0;i+2<raw.Length;i++)
            if(raw[i]=='%' && byte.TryParse(raw.AsSpan(i+1,2),System.Globalization.NumberStyles.HexNumber,null,out var value) && (value<32 || value==127))
                throw new InvalidDataException("Download URL contains control characters.");
    }
    internal static Uri ValidateInitial(ManifestFile file,string version)
    {
        if(file.Availability!="download" || file.Url==null) throw new InvalidDataException("File is not downloadable.");
        var uri=file.Url; ValidateHop(uri,file.Source);
        if(uri.Query.Length!=0) throw new InvalidDataException("Initial download query is forbidden.");
        if(file.Source=="github" && uri.AbsolutePath!=$"/Flinterino/wz-sync/releases/download/{version}/{file.Sha256}{Path.GetExtension(file.Path)}")
            throw new InvalidDataException("Download source does not match manifest metadata.");
        return uri;
    }
}
