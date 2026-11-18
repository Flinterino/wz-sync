using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WzSync.Core;

public sealed record RestoreEntry
{
    public string Path { get; }
    public FileSnapshot? Current { get; }
    public FileSnapshot? Desired { get; }
    public PlanAction Action => Current is null ? PlanAction.Add : Desired is null ? PlanAction.Remove :
        Current==Desired ? PlanAction.Keep : PlanAction.Update;

    internal RestoreEntry(string path, FileSnapshot? current, FileSnapshot? desired)
        => (Path,Current,Desired)=(path,current,desired);
    [JsonConstructor]
    internal RestoreEntry(string path, FileSnapshot? current, FileSnapshot? desired, PlanAction action)
        : this(path,current,desired) => JournalStore.Require(action==Action,"Journal action conflicts with its snapshots.");
    internal RestoreEntry Inverse() => new(Path,Desired,Current);
}

public sealed class RestorePlan
{
    public string ProfileRoot { get; }
    public ImmutableArray<RestoreEntry> Entries { get; }
    internal string Source { get; }
    internal ImmutableArray<string> Scope { get; }

    internal RestorePlan(ProfilePaths profile, OperationJournal journal)
    {
        ProfileRoot=profile.Root;
        Source=journal.Id;
        Scope=[..journal.Scope];
        Entries=[..journal.Entries.Select(e=>e.Inverse())];
    }
}

internal sealed record StateIndex(string? Active, string? Last);
internal sealed record OperationJournal(string Id, string? Source, string[] Scope, RestoreEntry[] Entries);

internal sealed class JournalStore(ProfilePaths paths)
{
    private const int MaximumBytes=8*1024*1024;
    private static readonly JsonSerializerOptions Options=new() {
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties=false,
        RespectNullableAnnotations=true, RespectRequiredConstructorParameters=true, MaxDepth=12,
        Converters={ new JsonStringEnumConverter<PlanAction>(allowIntegerValues:false) }
    };
    internal static string NewId() => Guid.NewGuid().ToString("N");
    internal static void Require(bool condition,string message)
    {
        if(!condition) throw new InvalidDataException(message);
    }
    private static bool IsId(string? id) => Guid.TryParseExact(id,"N",out var value) && value.ToString("N")==id;

    internal FileStream Lock(bool create)
    {
        if(create) Directory.CreateDirectory(Path.GetDirectoryName(paths.ResolveState("lock"))!);
        return new FileStream(paths.ResolveState("lock"),create?FileMode.OpenOrCreate:FileMode.Open,FileAccess.ReadWrite,FileShare.None);
    }

    internal StateIndex Index()
    {
        if(!Exists(paths.ResolveState("state.json"))) return new(null,null);
        var index=Read<StateIndex>("state.json",512);
        Require((index.Active==null || IsId(index.Active)) && (index.Last==null || IsId(index.Last)) &&
            (index.Active==null || index.Active!=index.Last),"Invalid transaction index.");
        return index;
    }
    internal void WriteIndex(StateIndex index) => Write("state.json",index,true);
    internal void Save(OperationJournal journal)
    {
        Validate(journal,journal.Id);
        Write($"backup/{journal.Id}/journal.json",journal,false);
    }
    internal OperationJournal Load(string id)
    {
        Require(IsId(id),"Invalid operation identity.");
        var journal=Read<OperationJournal>($"backup/{id}/journal.json",MaximumBytes);
        Validate(journal,id);
        return journal;
    }
    internal static void Validate(OperationJournal journal,string id)
    {
        Require(IsId(id) && journal.Id==id && (journal.Source==null || IsId(journal.Source) && journal.Source!=id),"Invalid journal identity.");
        Require(journal.Scope.Length<=10000 && journal.Entries.Length<=10000,"Journal exceeds entry limit.");
        var scopes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var scope in journal.Scope)
        {
            ManagedPolicy.ValidateRule(scope);
            Require(scopes.Add(scope),"Duplicate scope.");
        }
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in journal.Entries)
        {
            if(entry==null) throw new InvalidDataException("Invalid journal entry.");
            ManagedPolicy.ValidateFile(entry.Path);
            Require(ManagedPolicy.Includes(journal.Scope,entry.Path) && names.Add(entry.Path),"Invalid journal scope or duplicate path.");
            Require(entry.Current!=null || entry.Desired!=null,"Empty journal action.");
            foreach(var snapshot in new[]{entry.Current,entry.Desired})
                if(snapshot!=null) Require(snapshot.Size>=0 && snapshot.Sha256 is { Length:64 } hash &&
                    hash.All(c=>char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'),"Invalid journal integrity metadata.");
        }
    }

    internal static bool Exists(string full)
    {
        try {
            var attributes=File.GetAttributes(full);
            Require((attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))==0,"Expected a regular file.");
            return true;
        }
        catch(FileNotFoundException) { return false; }
        catch(DirectoryNotFoundException) { return false; }
    }
    private T Read<T>(string path,int max) where T:class
    {
        using var stream=new FileStream(paths.ResolveState(path),FileMode.Open,FileAccess.Read,FileShare.Read);
        var length=stream.Length;
        Require(length<=max,"Transaction state is too large.");
        var bytes=new byte[(int)length];
        stream.ReadExactly(bytes);
        Require(stream.ReadByte()==-1,"Transaction state changed during read.");
        try { return JsonSerializer.Deserialize<T>(bytes,Options) ?? throw new InvalidDataException("Empty transaction state."); }
        catch(JsonException) { throw new InvalidDataException("Malformed transaction state."); }
    }
    private void Write<T>(string path,T value,bool replace)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value,Options);
        Require(bytes.Length<=MaximumBytes,"Transaction state is too large.");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ResolveState(path))!);
        var tmp=path+"."+NewId()+".new";
        using(var stream=new FileStream(paths.ResolveState(tmp),FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            stream.Write(bytes); stream.Flush(true);
        }
        File.Move(paths.ResolveState(tmp),paths.ResolveState(path),replace);
    }
}

// Internal test seam to model process death between durable filesystem operations.
internal sealed class TransactionInterruptedException : Exception;
