namespace WzSync.Core;
internal static class UrlPolicy
{
    internal static void ValidateHop(Uri uri,string source,Uri? initial=null)
    {
        ValidateText(uri.OriginalString);
        if(!uri.IsAbsoluteUri || uri.Scheme!="https" || uri.Port!=443 || uri.UserInfo.Length!=0 || uri.Fragment.Length!=0)
            throw new InvalidDataException("Download URL is forbidden.");
        var allowed=source switch
        {
            "github" when uri.Host=="github.com" => initial==null || uri.AbsolutePath==initial.AbsolutePath,
            "github" => initial!=null && uri.Host is "objects.githubusercontent.com" or "release-assets.githubusercontent.com",
            "curseforge" => uri.Host is "edge.forgecdn.net" or "mediafilez.forgecdn.net",
            "modrinth" => uri.Host=="cdn.modrinth.com",
            _ => false
        };
        if(!allowed) throw new InvalidDataException("Download URL does not match the manifest source.");
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
