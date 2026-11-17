using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace WzSync.Core;

public sealed record ManifestFile
{
    public string Path { get; }
    public string Sha256 { get; }
    public long Size { get; }
    public string Source { get; }
    public Uri? Url { get; }
    public Uri? Page { get; }
    public string Availability { get; }
    public string? Reason { get; }
    internal ManifestFile(string path,string sha256,long size,string source,Uri? url,Uri? page,string availability,string? reason)
        => (Path,Sha256,Size,Source,Url,Page,Availability,Reason)=(path,sha256,size,source,url,page,availability,reason);
}
public sealed record SyncManifest
{
    public int Schema => 1;
    public string Pack { get; }
    public string Version { get; }
    public string Minecraft => "1.21.1";
    public string Loader => "neoforge-21.1.265";
    public ImmutableArray<string> Managed { get; }
    public ImmutableArray<ManifestFile> Files { get; }
    internal SyncManifest(string pack,string version,IEnumerable<string> managed,IEnumerable<ManifestFile> files)
        => (Pack,Version,Managed,Files)=(pack,version,managed.ToImmutableArray(),files.ToImmutableArray());
}
public static class ManifestParser
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public static SyncManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json)>MaximumBytes) throw new InvalidDataException("Manifest is too large.");
        try
        {
            using var document=JsonDocument.Parse(json,new JsonDocumentOptions { MaxDepth=16 });
            var root=document.RootElement;
            CheckObject(root,["schema","pack","version","minecraft","loader","managed","files"]);
            if(root.GetProperty("schema").GetInt32()!=1 || Text(root,"minecraft")!="1.21.1" || Text(root,"loader")!="neoforge-21.1.265") throw new InvalidDataException("Unsupported manifest target.");
            var pack=Text(root,"pack"); var version=Text(root,"version");
            if(pack is not ("full" or "lite") || !Regex.IsMatch(version,"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\\z") || version.EndsWith('.')) throw new InvalidDataException("Invalid pack or version.");
            var managed=root.GetProperty("managed").EnumerateArray().Select(x=>x.GetString() ?? throw new InvalidDataException("Null scope.")).ToArray();
            if(managed.Length>10000 || managed.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=managed.Length) throw new InvalidDataException("Duplicate managed scope.");
            foreach(var rule in managed) ManagedPolicy.ValidateRule(rule);
            var files=new List<ManifestFile>(); var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var row in root.GetProperty("files").EnumerateArray())
            {
                if(files.Count>=10000) throw new InvalidDataException("Too many files.");
                CheckObject(row,["path","sha256","size","source","url","page","availability","reason"]);
                var path=Text(row,"path"); ManagedPolicy.ValidateFile(path);
                if(!paths.Add(path)||!ManagedPolicy.Includes(managed,path)) throw new InvalidDataException("Duplicate or undeclared file path.");
                var hash=Text(row,"sha256"); var size=row.GetProperty("size").GetInt64();
                if(!Regex.IsMatch(hash,"^[a-fA-F0-9]{64}\\z") || size<0) throw new InvalidDataException("Invalid file integrity metadata.");
                var source=Text(row,"source"); var availability=Text(row,"availability");
                var url=Url(row,"url"); var page=Url(row,"page"); var reason=NullableText(row,"reason");
                if(source is not ("github" or "curseforge" or "modrinth" or "unresolved") || availability is not ("download" or "manual") ||
                   availability=="manual" && (url!=null||string.IsNullOrWhiteSpace(reason)) ||
                   availability=="download" && (url==null||reason!=null||source=="unresolved")) throw new InvalidDataException("Invalid availability metadata.");
                files.Add(new ManifestFile(path,hash.ToLowerInvariant(),size,source,url,page,availability,reason));
            }
            return new SyncManifest(pack,version,managed,files);
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new InvalidDataException("Malformed manifest.",e); }
    }
    private static void CheckObject(JsonElement value,string[] allowed)
    {
        if(value.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("Expected object.");
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var prop in value.EnumerateObject()) if(!allowed.Contains(prop.Name)||!seen.Add(prop.Name)) throw new InvalidDataException("Unknown or duplicate property.");
        if(seen.Count!=allowed.Length) throw new InvalidDataException("Missing property.");
    }
    private static string Text(JsonElement value,string key) => value.GetProperty(key).GetString() ?? throw new InvalidDataException("Null property.");
    private static string? NullableText(JsonElement value,string key) => value.GetProperty(key).ValueKind==JsonValueKind.Null ? null : Text(value,key);
    private static Uri? Url(JsonElement row,string key)
    {
        var text=NullableText(row,key); if(text==null) return null;
        if(!Uri.TryCreate(text,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!string.IsNullOrEmpty(uri.UserInfo)||uri.Port!=443||!string.IsNullOrEmpty(uri.Fragment)) throw new InvalidDataException("Invalid HTTPS URL.");
        return uri;
    }
}


