namespace WzSync.Core;

public sealed class TransactionEngine
{
    private readonly ProfilePaths paths;
    private readonly VerifiedDownloader downloader;
    private readonly JournalStore store;
    private readonly Action<string,string>? checkpoint;

    public TransactionEngine(ProfilePaths paths,VerifiedDownloader downloader) : this(paths,downloader,null) { }
    internal TransactionEngine(ProfilePaths paths,VerifiedDownloader downloader,Action<string,string>? checkpoint)
    {
        this.paths=paths; this.downloader=downloader; this.checkpoint=checkpoint;
        store=new JournalStore(paths);
    }

    public async Task ApplyAsync(SyncPlan plan,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RequireProfile(plan.ProfileRoot);
        if(!plan.CanApply) throw new InvalidOperationException("Manual requirements are outstanding.");
        cancellationToken.ThrowIfCancellationRequested();
        var entries=plan.Entries.Select(e=>new RestoreEntry(e.Path,e.Current,
            e.Desired is {} target? new FileSnapshot(target.Size,target.Sha256) : null)).ToArray();
        var journal=new OperationJournal(JournalStore.NewId(),null,[..plan.Manifest.Managed],entries);
        JournalStore.Validate(journal,journal.Id);
        void CheckPlan() => JournalStore.Require(plan.Entries.SequenceEqual(SyncPlanner.Create(paths,plan.Manifest).Entries),"The approved sync plan changed.");
        CheckPlan();
        using var lease=store.Lock(true);
        var index=Idle();
        if(entries.All(e=>e.Action==PlanAction.Keep)) { CheckPlan(); return; }
        using var log=new LocalLog(paths,journal.Id);
        log.Write("apply");
        for(var i=0;i<entries.Length;i++)
        {
            if(entries[i].Action is not (PlanAction.Add or PlanAction.Update)) continue;
            log.Write("download",entries[i].Path);
            await downloader.StageAsync(paths,plan.Entries[i].Desired!,plan.Manifest.Version,Stage(journal,i),cancellationToken).ConfigureAwait(false);
            log.Write("staged",entries[i].Path);
            checkpoint?.Invoke("staged",entries[i].Path);
        }
        Commit(index,journal,log,CheckPlan,cancellationToken);
    }
    public RestorePlan PrepareRestoreLast()
    {
        if(!JournalStore.Exists(paths.ResolveState("lock"))) throw new InvalidOperationException("No restore point.");
        using var lease=store.Lock(false);
        return Preview(Idle());
    }
    public Task RestoreAsync(RestorePlan plan,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RequireProfile(plan.ProfileRoot);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease=store.Lock(false);
        var index=Idle();
        void CheckPlan()
        {
            var fresh=Preview(index);
            JournalStore.Require(fresh.Source==plan.Source && fresh.Scope.SequenceEqual(plan.Scope) &&
                fresh.Entries.SequenceEqual(plan.Entries),"The approved restore plan changed.");
        }
        CheckPlan();
        var journal=new OperationJournal(JournalStore.NewId(),plan.Source,[..plan.Scope],[..plan.Entries]);
        using var log=new LocalLog(paths,journal.Id);
        log.Write("restore");
        for(var i=0;i<journal.Entries.Length;i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row=journal.Entries[i];
            if(row.Action is not (PlanAction.Add or PlanAction.Update)) continue;
            CopyState(Backup(plan.Source,i),Stage(journal,i),row.Desired!);
            log.Write("staged",row.Path);
            checkpoint?.Invoke("staged",row.Path);
        }
        Commit(index,journal,log,CheckPlan,cancellationToken);
        return Task.CompletedTask;
    }
    public bool Recover()
    {
        if(!JournalStore.Exists(paths.ResolveState("lock")))
        {
            JournalStore.Require(!JournalStore.Exists(paths.ResolveState("state.json")),"Transaction lock is missing.");
            store.Index();
            return false;
        }
        using var lease=store.Lock(false);
        var index=store.Index();
        if(index.Active==null) return false;
        var journal=store.Load(index.Active);
        if(journal.Source!=null)
        {
            JournalStore.Require(index.Last==journal.Source,"Restore source changed.");
            var source=store.Load(journal.Source);
            JournalStore.Require(source.Source==null && source.Scope.SequenceEqual(journal.Scope) &&
                source.Entries.Select(e=>e.Inverse()).SequenceEqual(journal.Entries),"Restore journal is inconsistent.");
        }
        using var log=new LocalLog(paths,journal.Id);
        log.Write("recover");
        Rollback(index,journal,log);
        return true;
    }
    private void RequireProfile(string root)
    {
        if(root!=paths.Root) throw new InvalidOperationException("Plan belongs to a different profile.");
    }
    private StateIndex Idle()
    {
        var index=store.Index();
        if(index.Active!=null) throw new InvalidOperationException("Interrupted transaction requires recovery.");
        return index;
    }
    private RestorePlan Preview(StateIndex index)
    {
        if(index.Last==null) throw new InvalidOperationException("No restore point.");
        var source=store.Load(index.Last);
        JournalStore.Require(source.Source==null,"Invalid restore point.");
        VerifyPoststate(source);
        return new RestorePlan(paths,source);
    }
    private static string Stage(OperationJournal j,int i) => $"tmp/{j.Id}/{i}.part";
    private static string Backup(string id,int i) => $"backup/{id}/{i}.original";
    private static FileSnapshot? Snapshot(string full) => JournalStore.Exists(full) ? FileSnapshot.Capture(full) : null;
    private static void Expect(string full,FileSnapshot? expected) =>
        JournalStore.Require(Snapshot(full)==expected,"File contents differ from the approved transaction.");

    private void VerifyPoststate(OperationJournal journal)
    {
        // Reuse the read-only planner to enumerate precisely the declared scopes, including newly arrived files.
        var observed=SyncPlanner.Inspect(paths,journal.Scope,[]).Where(e=>e.Current!=null).ToArray();
        var expected=journal.Entries.Where(e=>e.Desired!=null).ToDictionary(e=>e.Path,e=>e.Desired,StringComparer.Ordinal);
        JournalStore.Require(observed.Length==expected.Count && observed.All(e=>expected.TryGetValue(e.Path,out var s) && e.Current==s),
            "Current managed files differ from the transaction poststate.");
        for(var i=0;i<journal.Entries.Length;i++)
        {
            var row=journal.Entries[i];
            if(row.Action is PlanAction.Update or PlanAction.Remove)
                Expect(paths.ResolveState(Backup(journal.Id,i)),row.Current);
        }
    }
    private void Commit(StateIndex previous,OperationJournal journal,LocalLog log,Action checkPlan,CancellationToken token)
    {
        for(var i=0;i<journal.Entries.Length;i++)
        {
            var row=journal.Entries[i];
            if(row.Action is PlanAction.Add or PlanAction.Update) Expect(paths.ResolveState(Stage(journal,i)),row.Desired);
            Expect(paths.ResolveState(Backup(journal.Id,i)),null);
        }
        checkPlan();
        token.ThrowIfCancellationRequested();
        JournalStore.Require(store.Index()==previous,"Transaction index changed during staging.");
        if(!JournalStore.Exists(paths.ResolveState("state.json"))) store.WriteIndex(previous);
        store.Save(journal);
        var active=previous with { Active=journal.Id };
        store.WriteIndex(active);
        try
        {
            // Evacuate every original before installing the first replacement.
            for(var i=0;i<journal.Entries.Length;i++)
            {
                var row=journal.Entries[i];
                if(row.Action==PlanAction.Keep) log.Write("keep",row.Path);
                if(row.Action is not (PlanAction.Update or PlanAction.Remove)) continue;
                var backup=Backup(journal.Id,i);
                Expect(paths.ResolveManaged(row.Path),row.Current);
                File.Move(paths.ResolveManaged(row.Path),paths.ResolveState(backup));
                log.Write(row.Action==PlanAction.Remove ? "remove-to-backup" : "backup",row.Path);
                checkpoint?.Invoke("backup",row.Path);
                Expect(paths.ResolveState(backup),row.Current);
            }
            for(var i=0;i<journal.Entries.Length;i++)
            {
                var row=journal.Entries[i];
                if(row.Action is not (PlanAction.Add or PlanAction.Update)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(paths.ResolveManaged(row.Path))!);
                Expect(paths.ResolveManaged(row.Path),null);
                Expect(paths.ResolveState(Stage(journal,i)),row.Desired);
                File.Move(paths.ResolveState(Stage(journal,i)),paths.ResolveManaged(row.Path));
                log.Write("install",row.Path);
                checkpoint?.Invoke("install",row.Path);
            }
            VerifyPoststate(journal);
            log.Write("verified");
            // One atomic rename both records completion and disarms recovery.
            store.WriteIndex(new StateIndex(null,journal.Source==null?journal.Id:null));
        }
        catch(Exception e) when(e is not TransactionInterruptedException)
        {
            Rollback(active,journal,log);
            throw;
        }
        checkpoint?.Invoke("committed","");
    }
    private void Rollback(StateIndex active,OperationJournal journal,LocalLog log)
    {
        // Preflight all conflicts before moving anything. Check again at each individual move.
        for(var i=0;i<journal.Entries.Length;i++) CheckRollbackRow(journal,i);
        for(var i=journal.Entries.Length-1;i>=0;i--)
        {
            var row=journal.Entries[i];
            var current=CheckRollbackRow(journal,i);
            if(current==row.Current) continue;
            if(current!=null)
            {
                Expect(paths.ResolveManaged(row.Path),current);
                File.Move(paths.ResolveManaged(row.Path),paths.ResolveState($"backup/{journal.Id}/{i}.installed"));
                Expect(paths.ResolveState($"backup/{journal.Id}/{i}.installed"),current);
                log.Write("rollback-save",row.Path);
                checkpoint?.Invoke("rollback-save",row.Path);
            }
            if(row.Current!=null)
            {
                var temp=$"tmp/{journal.Id}/{JournalStore.NewId()}.restore";
                CopyState(Backup(journal.Id,i),temp,row.Current);
                Expect(paths.ResolveManaged(row.Path),null);
                Expect(paths.ResolveState(temp),row.Current);
                File.Move(paths.ResolveState(temp),paths.ResolveManaged(row.Path));
                log.Write("rollback-restore",row.Path);
                checkpoint?.Invoke("rollback-restore",row.Path);
            }
        }
        foreach(var row in journal.Entries) Expect(paths.ResolveManaged(row.Path),row.Current);
        log.Write("rolled-back");
        store.WriteIndex(active with { Active=null });
    }
    private FileSnapshot? CheckRollbackRow(OperationJournal j,int i)
    {
        var row=j.Entries[i];
        var current=Snapshot(paths.ResolveManaged(row.Path));
        var saved=Snapshot(paths.ResolveState($"backup/{j.Id}/{i}.installed"));
        JournalStore.Require(saved==null || row.Action is PlanAction.Add or PlanAction.Update && saved==row.Desired,
            "Saved installed bytes conflict with recovery.");
        var original=Snapshot(paths.ResolveState(Backup(j.Id,i)));
        JournalStore.Require(original==null || row.Action!=PlanAction.Keep && original==row.Current,"Backup integrity conflict during recovery.");
        if(current==row.Current) return current;
        JournalStore.Require(row.Action!=PlanAction.Keep && (current==null || current==row.Desired) &&
            original==row.Current,"Recovery requires intact originals and unchanged managed files.");
        if(current!=null)
        {
            Expect(paths.ResolveState(Stage(j,i)),null);
            Expect(paths.ResolveState($"backup/{j.Id}/{i}.installed"),null);
        }
        return current;
    }
    private void CopyState(string source,string dest,FileSnapshot expected)
    {
        Expect(paths.ResolveState(source),expected);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ResolveState(dest))!);
        using(var input=new FileStream(paths.ResolveState(source),FileMode.Open,FileAccess.Read,FileShare.Read))
        using(var output=new FileStream(paths.ResolveState(dest),FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            var buffer=new byte[81920];
            long remaining=expected.Size;
            while(remaining>0)
            {
                var count=input.Read(buffer,0,(int)Math.Min(remaining,buffer.Length));
                JournalStore.Require(count>0,"Backup ended during staging.");
                output.Write(buffer,0,count);
                remaining-=count;
            }
            JournalStore.Require(input.ReadByte()==-1,"Backup grew during staging.");
            output.Flush(true);
        }
        Expect(paths.ResolveState(dest),expected);
    }

    private sealed class LocalLog : IDisposable
    {
        private readonly ProfilePaths paths;
        private readonly string path;
        private readonly FileStream stream;
        internal LocalLog(ProfilePaths paths,string operation)
        {
            this.paths=paths;
            path="sync-"+DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'",System.Globalization.CultureInfo.InvariantCulture)+"-"+operation+".log";
            stream=new FileStream(paths.ResolveState(path),FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        }
        internal void Write(string phase,string? relative=null)
        {
            paths.ResolveState(path);
            var line=DateTimeOffset.UtcNow.ToString("O",System.Globalization.CultureInfo.InvariantCulture)+" "+phase;
            if(relative!=null) line+=" "+relative;
            stream.Write(System.Text.Encoding.UTF8.GetBytes(line+"\n"));
            stream.Flush();
        }
        public void Dispose()
        {
            // Each record is flushed when written. Closing a log cannot change a committed outcome.
            try { stream.Dispose(); } catch(IOException) { }
        }
    }
}
