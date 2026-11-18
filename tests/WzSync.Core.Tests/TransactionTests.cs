using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WzSync.Core;

namespace WzSync.Core.Tests;

public sealed class TransactionTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"wzsync-tx-"+Guid.NewGuid().ToString("N"));
    private readonly ProfilePaths paths;
    private readonly VerifiedDownloader downloader;
    private readonly Dictionary<string,byte[]> payloads=[];
    private Action? onDownload;

    public TransactionTests()
    {
        Directory.CreateDirectory(root);
        paths=new ProfilePaths(root);
        downloader=new VerifiedDownloader(new Handler(request=>{
            onDownload?.Invoke();
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content=new ByteArrayContent(payloads[request.RequestUri!.Segments.Last()]) };
        }));
    }
    public void Dispose() { downloader.Dispose(); Directory.Delete(root,true); }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            => Task.FromResult(reply(request));
    }
    private void Put(string path,string value)
    {
        var file=Path.Combine(root,path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file,value);
    }
    private string? Read(string path) => File.Exists(Path.Combine(root,path)) ? File.ReadAllText(Path.Combine(root,path)) : null;
    private TransactionEngine Engine(Action<string,string>? checkpoint=null) => new(paths,downloader,checkpoint);
    private ManifestFile Row(string name,string value,bool manual=false)
    {
        var bytes=Encoding.UTF8.GetBytes(value);
        var hash=Convert.ToHexStringLower(SHA256.HashData(bytes));
        payloads[hash]=bytes;
        return new("mods/"+name,hash,bytes.Length,"modrinth",manual?null:new Uri("https://cdn.modrinth.com/"+hash),null,manual?"manual":"download",manual?"manual":null);
    }
    private SyncPlan Plan(params ManifestFile[] rows) => SyncPlanner.Create(paths,new SyncManifest("full","v1",["mods/"],rows));
    private SyncPlan Fixture()
    {
        Put("mods/b.jar","old"); Put("mods/c.jar","removed"); Put("mods/k.jar","keep");
        Put("options.txt","options"); Put("saves/world/level.dat","world");
        return Plan(Row("a.jar","added"),Row("b.jar","updated"),Row("k.jar","keep",true));
    }
    private void Original()
    {
        Assert.Null(Read("mods/a.jar")); Assert.Equal("old",Read("mods/b.jar")); Assert.Equal("removed",Read("mods/c.jar"));
        Assert.Equal("keep",Read("mods/k.jar")); Assert.Equal("options",Read("options.txt")); Assert.Equal("world",Read("saves/world/level.dat"));
    }
    [Fact] public async Task Restores_complete_inverse_and_preserves_both_backup_sets()
    {
        var engine=Engine(); await engine.ApplyAsync(Fixture());
        Assert.Equal("added",Read("mods/a.jar")); Assert.Equal("updated",Read("mods/b.jar")); Assert.Null(Read("mods/c.jar"));
        var restore=engine.PrepareRestoreLast();
        Assert.Equal([PlanAction.Remove,PlanAction.Update,PlanAction.Add,PlanAction.Keep],restore.Entries.Select(e=>e.Action));
        await engine.RestoreAsync(restore); Original();
        Assert.Throws<InvalidOperationException>(()=>engine.PrepareRestoreLast());
        var backupIds=Directory.GetDirectories(Path.Combine(root,".wzsync/backup")).Select(Path.GetFileName);
        foreach(var id in backupIds) Assert.Single(Directory.GetFiles(Path.Combine(root,".wzsync"),$"sync-*-{id}.log"));
        var versions=Directory.GetFiles(Path.Combine(root,".wzsync/backup"),"*.original",SearchOption.AllDirectories).Select(File.ReadAllText);
        Assert.Equal(["added","old","removed","updated"],versions.Order(StringComparer.Ordinal));
        var logs=string.Join("\n",Directory.GetFiles(Path.Combine(root,".wzsync"),"sync-*.log").Select(File.ReadAllText));
        Assert.Contains("mods/b.jar",logs); Assert.DoesNotContain(root,logs); Assert.DoesNotContain("https",logs);
    }
    [Theory] [InlineData("backup")] [InlineData("install")]
    public async Task Failure_rolls_back_and_disarms_recovery(string phase)
    {
        var engine=Engine((point,_)=>{if(point==phase) throw new IOException("fixture");});
        await Assert.ThrowsAsync<IOException>(()=>engine.ApplyAsync(Fixture())); Original();
        Assert.False(Engine().Recover());
    }
    [Theory] [InlineData("backup")] [InlineData("install")]
    public async Task Recovery_after_abrupt_interruption_is_idempotent(string phase)
    {
        var engine=Engine((point,_)=>{if(point==phase) throw new TransactionInterruptedException();});
        await Assert.ThrowsAsync<TransactionInterruptedException>(()=>engine.ApplyAsync(Fixture()));
        var next=Engine();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>next.ApplyAsync(Plan()));
        Assert.True(next.Recover()); Original(); Assert.False(next.Recover());
    }
    [Fact] public async Task Unknown_current_bytes_block_recovery_without_overwrite()
    {
        var engine=Engine((point,_)=>{if(point=="install") throw new TransactionInterruptedException();});
        await Assert.ThrowsAsync<TransactionInterruptedException>(()=>engine.ApplyAsync(Fixture()));
        Put("mods/a.jar","user edits"); Assert.Throws<InvalidDataException>(()=>Engine().Recover());
        Assert.Equal("user edits",Read("mods/a.jar"));
    }
    [Fact] public async Task Manual_or_other_profile_refused_before_state_creation()
    {
        var engine=Engine(); await Assert.ThrowsAsync<InvalidOperationException>(()=>engine.ApplyAsync(Plan(Row("m","absent",true))));
        Directory.CreateDirectory(Path.Combine(root,"other"));
        var other=SyncPlanner.Create(new ProfilePaths(Path.Combine(root,"other")),new SyncManifest("full","v1",["mods/"],[]));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>engine.ApplyAsync(other));
        Assert.False(Directory.Exists(Path.Combine(root,".wzsync")));
    }
    [Theory] [InlineData("a.jar")] [InlineData("unplanned.jar")] [InlineData("k.jar")]
    public async Task Revalidates_entire_plan_after_download(string name)
    {
        var plan=Fixture(); onDownload=()=>Put("mods/"+name,"concurrent");
        await Assert.ThrowsAsync<InvalidDataException>(()=>Engine().ApplyAsync(plan));
        Assert.Equal("old",Read("mods/b.jar")); Assert.Equal("removed",Read("mods/c.jar")); Assert.Equal("concurrent",Read("mods/"+name));
    }
    [Fact] public async Task Bad_second_download_or_tampered_stage_never_mutates_managed_bytes()
    {
        var plan=Fixture(); payloads[plan.Entries[1].Desired!.Sha256]=[1];
        await Assert.ThrowsAsync<InvalidDataException>(()=>Engine().ApplyAsync(plan)); Original();
        plan=Fixture();
        var engine=Engine((point,path)=>{if(point=="staged" && path=="mods/b.jar"){
            var dir=Directory.GetDirectories(Path.Combine(root,".wzsync/tmp")).Single(d=>File.Exists(Path.Combine(d,"1.part")) && File.ReadAllText(Path.Combine(d,"1.part"))=="updated");
            File.WriteAllText(Path.Combine(dir,"0.part"),"tampered");
        }});
        await Assert.ThrowsAsync<InvalidDataException>(()=>engine.ApplyAsync(plan)); Original();
    }
    [Theory] [InlineData("changed")] [InlineData("new")] [InlineData("corrupt")] [InlineData("missing")]
    public async Task Restore_rechecks_approval_and_backup(string conflict)
    {
        var engine=Engine(); await engine.ApplyAsync(Fixture()); var plan=engine.PrepareRestoreLast();
        if(conflict=="changed") Put("mods/b.jar","user edits");
        if(conflict=="new") Put("mods/new.jar","new arrival");
        if(conflict is "corrupt" or "missing"){
            var backup=Directory.GetFiles(Path.Combine(root,".wzsync/backup"),"1.original",SearchOption.AllDirectories).Single();
            if(conflict=="missing") File.Delete(backup); else File.WriteAllText(backup,"bad");
        }
        await Assert.ThrowsAsync<InvalidDataException>(()=>engine.RestoreAsync(plan));
        Assert.Equal("added",Read("mods/a.jar")); Assert.Equal(conflict=="changed"?"user edits":"updated",Read("mods/b.jar"));
    }
    [Fact] public async Task Lock_excludes_second_apply_until_first_completes()
    {
        var plan=Fixture(); bool attempted=false;
        onDownload=()=>{attempted=true; Assert.Throws<IOException>(()=>Engine().Recover());};
        await Engine().ApplyAsync(plan); Assert.True(attempted);
    }
    [Fact] public async Task Recovery_rejects_malformed_unsafe_or_oversized_state_before_moving_bytes()
    {
        var crash=Engine((phase,_)=>{if(phase=="install") throw new TransactionInterruptedException();});
        await Assert.ThrowsAsync<TransactionInterruptedException>(()=>crash.ApplyAsync(Fixture()));
        var index=Path.Combine(root,".wzsync/state.json");
        var savedIndex=File.ReadAllText(index);
        var id=JsonNode.Parse(savedIndex)!["Active"]!.GetValue<string>();
        var journal=Path.Combine(root,".wzsync","backup",id,"journal.json");
        var savedJournal=File.ReadAllText(journal);
        foreach(var bad in new[]{"../escape","mods/../saves/level.dat","mods/CON.jar",".wzsync/lock","options.txt",Path.Combine(root,"outside")})
        {
            var obj=JsonNode.Parse(savedJournal)!;
            obj["Entries"]![0]!["Path"]=bad;
            File.WriteAllText(journal,obj.ToJsonString());
            Assert.Throws<InvalidDataException>(()=>Engine().Recover());
        }
        foreach(var change in new Action<JsonNode>[] {
            o=>o["Surprise"]=true,
            o=>o["Id"]=Guid.NewGuid().ToString("N"),
            o=>o["Source"]=Guid.NewGuid().ToString("N"),
            o=>o["Scope"]![0]="saves/",
            o=>o["Entries"]![0]!["Desired"]!["Size"]=-1,
            o=>o["Entries"]![0]!["Desired"]!["Sha256"]="short",
            o=>o["Entries"]![0]!["Desired"]=null,
            o=>o["Entries"]![1]!["Path"]="mods/A.jar",
            o=>o["Entries"]![0]!["Action"]="Remove",
            o=>o["Entries"]=new JsonArray(Enumerable.Repeat<JsonNode?>(null,10001).ToArray())
        })
        {
            var obj=JsonNode.Parse(savedJournal)!; change(obj); File.WriteAllText(journal,obj.ToJsonString());
            Assert.Throws<InvalidDataException>(()=>Engine().Recover());
        }
        File.WriteAllText(journal,"{\"Id\":\""+id+"\","+savedJournal[1..]);
        Assert.Throws<InvalidDataException>(()=>Engine().Recover());
        using(var oversized=new FileStream(journal,FileMode.Create,FileAccess.Write)) oversized.SetLength(8*1024*1024+1);
        Assert.Throws<InvalidDataException>(()=>Engine().Recover());
        File.WriteAllText(journal,savedJournal);
        foreach(var bad in new[]{"{\"Active\":\"../elsewhere\",\"Last\":null}","{\"Last\":null}", "{\"Active\":null,\"Last\":null,\"Last\":null}"}){
            File.WriteAllText(index,bad);
            Assert.Throws<InvalidDataException>(()=>Engine().Recover());
        }
        Assert.Equal("added",Read("mods/a.jar")); Assert.Null(Read("mods/b.jar")); Assert.Null(Read("mods/c.jar"));
        File.WriteAllText(index,savedIndex); Assert.True(Engine().Recover()); Original();
    }
    [Fact] public async Task Interrupted_restore_rolls_back_to_the_apply_receipt()
    {
        var engine=Engine(); await engine.ApplyAsync(Fixture()); var restore=engine.PrepareRestoreLast();
        var crash=Engine((point,_)=>{if(point=="install") throw new TransactionInterruptedException();});
        await Assert.ThrowsAsync<TransactionInterruptedException>(()=>crash.RestoreAsync(restore));
        Assert.True(engine.Recover()); Assert.Equal("added",Read("mods/a.jar")); Assert.Equal("updated",Read("mods/b.jar"));
        Assert.Null(Read("mods/c.jar")); Assert.Equal(restore.Entries.ToArray(),engine.PrepareRestoreLast().Entries.ToArray());
        await engine.RestoreAsync(restore); Original();
    }
    [Fact] public async Task Recovery_resumes_after_a_second_interruption_during_rollback()
    {
        await Assert.ThrowsAsync<TransactionInterruptedException>(()=>Engine((p,_)=>{if(p=="install") throw new TransactionInterruptedException();}).ApplyAsync(Fixture()));
        Assert.Throws<TransactionInterruptedException>(()=>Engine((p,_)=>{if(p=="rollback-restore") throw new TransactionInterruptedException();}).Recover());
        Assert.True(Engine().Recover()); Original();
    }
    [Fact] public async Task Missing_backup_after_crash_blocks_recovery_and_new_apply()
    {
        await Assert.ThrowsAsync<TransactionInterruptedException>(()=>Engine((p,_)=>{if(p=="backup") throw new TransactionInterruptedException();}).ApplyAsync(Fixture()));
        var file=Directory.GetFiles(Path.Combine(root,".wzsync/backup"),"*.original",SearchOption.AllDirectories).Single();
        var backup=File.ReadAllBytes(file); File.Delete(file);
        Assert.Throws<InvalidDataException>(()=>Engine().Recover()); Assert.Null(Read("mods/b.jar"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Engine().ApplyAsync(Plan()));
        File.WriteAllBytes(file,backup); Assert.True(Engine().Recover()); Original();
    }
    [Fact] public async Task Noop_apply_leaves_the_original_restore_point_available()
    {
        var engine=Engine(); await engine.ApplyAsync(Fixture()); var first=engine.PrepareRestoreLast();
        var noop=Plan(Row("a.jar","added"),Row("b.jar","updated"),Row("k.jar","keep",true));
        await engine.ApplyAsync(noop); await engine.RestoreAsync(first); Original();
    }
    [Theory] [InlineData("staged")] [InlineData("backup")]
    public async Task Cancellation_before_and_after_mutation_boundary(string phase)
    {
        var plan=Fixture(); using var cancel=new CancellationTokenSource();
        var engine=Engine((p,_)=>{if(p==phase) cancel.Cancel();});
        if(phase=="staged"){
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>engine.ApplyAsync(plan,cancel.Token)); Original();
        } else {
            await engine.ApplyAsync(plan,cancel.Token); Assert.Equal("added",Read("mods/a.jar"));
            await engine.RestoreAsync(engine.PrepareRestoreLast()); Original();
        }
    }
    private static void LinkDirectory(string link,string target)
    {
        if(!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link,target); return; }
        var start=new System.Diagnostics.ProcessStartInfo("cmd.exe",$"/c mklink /J \"{link}\" \"{target}\"") {
            CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true
        };
        using var process=System.Diagnostics.Process.Start(start)!;
        process.WaitForExit(); Assert.Equal(0,process.ExitCode);
    }
    [Fact] public async Task Managed_parent_replaced_by_junction_during_download_is_refused()
    {
        var plan=Fixture(); var mods=Path.Combine(root,"mods"); var old=Path.Combine(root,"old-mods");
        var outside=Directory.CreateDirectory(Path.Combine(root,"outside")).FullName;
        onDownload=()=>{ if(Directory.Exists(old)) return; Directory.Move(mods,old); LinkDirectory(mods,outside); };
        try {
            await Assert.ThrowsAsync<InvalidDataException>(()=>Engine().ApplyAsync(plan));
            Assert.Empty(Directory.GetFileSystemEntries(outside)); Assert.Equal("old",Read("old-mods/b.jar"));
        } finally { if(Directory.Exists(old)) { Directory.Delete(mods); Directory.Move(old,mods); } }
    }
    [Fact] public async Task Recovery_refuses_redirected_journal_directory()
    {
        await Assert.ThrowsAsync<TransactionInterruptedException>(()=>Engine((p,_)=>{if(p=="backup") throw new TransactionInterruptedException();}).ApplyAsync(Fixture()));
        var target=Directory.GetDirectories(Path.Combine(root,".wzsync/backup")).Single();
        var other=Path.Combine(root,"elsewhere"); Directory.Move(target,other);
        LinkDirectory(target,other);
        try {
            Assert.Throws<InvalidDataException>(()=>Engine().Recover());
            Assert.Null(Read("mods/b.jar"));
        } finally { Directory.Delete(target); Directory.Move(other,target); }
        Assert.True(Engine().Recover()); Original();
    }
    [Fact] public async Task Narrow_scope_restores_exact_config_and_ignores_other_mods_and_configs()
    {
        Put("config/sodium-options.json","previous"); Put("mods/excluded.jar","outside");
        Put("config/private.json","private");
        var source=Row("unused.jar","desired");
        var config=new ManifestFile("config/sodium-options.json",source.Sha256,source.Size,source.Source,source.Url,null,"download",null);
        var manifest=new SyncManifest("lite","some-release",["mods/a.jar",config.Path],[config]);
        var engine=Engine(); await engine.ApplyAsync(SyncPlanner.Create(paths,manifest));
        Assert.Equal("desired",Read(config.Path)); var restore=engine.PrepareRestoreLast();
        Put("mods/excluded.jar","edit outside scope"); await engine.RestoreAsync(restore);
        Assert.Equal("previous",Read(config.Path)); Assert.Equal("edit outside scope",Read("mods/excluded.jar"));
        Assert.Equal("private",Read("config/private.json"));
    }
    [Fact] public async Task Ordinary_failure_with_concurrent_edit_retains_recovery_intent()
    {
        var engine=Engine((phase,path)=>{if(phase=="install"){Put(path,"unknown"); throw new IOException("injected");}});
        await Assert.ThrowsAsync<InvalidDataException>(()=>engine.ApplyAsync(Fixture()));
        Assert.Equal("unknown",Read("mods/a.jar"));
        Assert.Throws<InvalidDataException>(()=>Engine().Recover());
        Put("mods/a.jar","added"); Assert.True(Engine().Recover()); Original();
    }
    [Fact] public async Task Crash_after_commit_pointer_cannot_undo_completed_operation()
    {
        var crash=Engine((phase,_)=>{if(phase=="committed") throw new TransactionInterruptedException();});
        await Assert.ThrowsAsync<TransactionInterruptedException>(()=>crash.ApplyAsync(Fixture()));
        var next=Engine(); Assert.False(next.Recover()); await next.RestoreAsync(next.PrepareRestoreLast()); Original();
    }
    [Fact] public async Task Unsafe_new_managed_directory_refused_before_state_creation()
    {
        var plan=Fixture(); Directory.CreateDirectory(Path.Combine(root,"mods/nested"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>Engine().ApplyAsync(plan));
        Assert.False(Directory.Exists(Path.Combine(root,".wzsync"))); Original();
    }
}
