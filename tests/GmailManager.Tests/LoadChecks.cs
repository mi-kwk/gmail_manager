using System.Diagnostics;
using GmailManager.Core;

static class LoadChecks
{
    public static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"GmailManager.LoadChecks",Guid.NewGuid().ToString("N"));
        var store=new Store(root);
        var account=new Account("load-account","load@example.com","負荷試験");
        store.Put("account",account.Id,account);
        var mails=Enumerable.Range(0,5000).Select(i=>new Mail(account.Id,i.ToString(),
            i%2==0?"target@example.com":"other@example.com","負荷試験 "+i,DateTimeOffset.Now,i%3==0?["INBOX","UNREAD"]:[])).ToArray();
        var timer=Stopwatch.StartNew();
        store.ReplaceMail(account,mails);
        var loaded=store.All<Mail>("mail");
        var matching=MailEngine.SenderMatches(loaded,"target@example.com").ToArray();
        if(loaded.Count!=5000 || matching.Length!=2500) throw new Exception("大量メールの保存・完全一致件数が不正");
        Console.WriteLine($"LOAD snapshot/filter: 5000 mails, 2500 matches, {timer.ElapsedMilliseconds} ms");
        var gateway=new FakeGateway();
        var engine=new MailEngine(store,gateway);
        var progress=new InlineProgress();
        timer.Restart();
        var operation=await engine.ExecuteAsync(matching.Take(1000),"アーカイブ",null,progress,CancellationToken.None);
        if(operation.Items.Any(i=>i.State!="成功") || progress.Last?.Completed!=1000) throw new Exception("分割処理の結果・進捗が不正");
        var persisted=store.Get<Operation>("operation",operation.Id)!;
        if(persisted.Items.Count!=1000 || persisted.Items.Any(i=>i.State!="成功")) throw new Exception("大量処理の履歴が不正");
        Console.WriteLine($"LOAD operation/history: 1000 mails, {timer.ElapsedMilliseconds} ms; database {new FileInfo(Path.Combine(root,"gmail.db")).Length} bytes");
        var cancellation=new CancellationTokenSource();
        int completed=0;
        gateway.AfterChange=()=> { if(++completed==51) cancellation.Cancel(); };
        operation=await engine.ExecuteAsync(matching.Take(120),"アーカイブ",null,null,cancellation.Token);
        if(operation.Items.Count(i=>i.State=="成功")!=51 || operation.Items.Count(i=>i.State=="未処理")!=69) throw new Exception("分割境界後のキャンセルが不正");
        Console.WriteLine("PASS: cancellation after crossing the 50-item boundary preserves completed items");
        gateway.AfterChange=null;
        var labelMail=mails[1];
        operation=await engine.ExecuteAsync([labelMail],"ラベル","Label_test",null,CancellationToken.None);
        if(operation.Label!="Label_test" || !store.Get<Mail>("mail",labelMail.Key)!.Labels.Contains("Label_test")) throw new Exception("ラベル結果・履歴が不正");
        Console.WriteLine("PASS: label operation persists selected label and local result");
        // Every terminal backup state, including queued work, must exclude deletion.
        foreach(var state in new[]{"保存待ち","保存中","失敗","保存完了"})
        {
            store.Put("backup",labelMail.Key,new Backup(labelMail,true,state,null,null));
            operation=await engine.ExecuteAsync([labelMail],"ゴミ箱移動",null,null,CancellationToken.None);
            if(operation.Items[0].State!="保護除外") throw new Exception("保護が失われました: "+state);
        }
        Console.WriteLine("PASS: queued, running, failed and completed backups all protect deletion");
        // A per-item journal must be visible before final compaction, including after restart.
        var journal=new Operation("journal",DateTimeOffset.Now,"ゴミ箱移動",
            [new(mails[0],"未処理"),new(mails[1],"未処理"),new(mails[2],"未処理")]);
        store.Put("operation",journal.Id,journal);
        journal.Items[0]=journal.Items[0] with { State="成功" }; store.SaveOperationItem(journal,0);
        journal.Items[1]=journal.Items[1] with { State="実行中" }; store.SaveOperationItem(journal,1);
        if(store.Get<Operation>("operation",journal.Id)!.Items[0].State!="成功") throw new Exception("差分履歴が取得できません");
        var recovered=new Store(root).Get<Operation>("operation",journal.Id)!;
        if(recovered.Items[0].State!="成功" || recovered.Items[1].State!="要確認" || recovered.Items[2].State!="未処理")
            throw new Exception("差分履歴の再起動回復が不正");
        if(store.All<Operation>("operation").Single(o=>o.Id==journal.Id).Items[1].State!="要確認")
            throw new Exception("履歴一覧の回復が不正");
        Console.WriteLine("PASS: per-item history survives restart and preserves success, uncertain and unprocessed states");
        var blocking=new BlockingGateway();
        var blockedEngine=new MailEngine(store,blocking);
        using var stop=new CancellationTokenSource();
        var pending=blockedEngine.ExecuteAsync(mails.Take(3),"アーカイブ",null,null,stop.Token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool overlapRejected=false;
        try { await blockedEngine.ExecuteAsync([mails[3]],"アーカイブ",null,null,CancellationToken.None); }
        catch(InvalidOperationException) { overlapRejected=true; }
        if(!overlapRejected) throw new Exception("二重実行を拒否できません");
        stop.Cancel();
        if(pending.IsCompleted) throw new Exception("送信済みリクエストの結果を待っていません");
        blocking.Release.SetResult();
        var stopped=await pending;
        if(stopped.Items[0].State!="成功" || stopped.Items.Skip(1).Any(i=>i.State!="未処理"))
            throw new Exception("実行中キャンセルの結果が不正");
        Console.WriteLine("PASS: cancellation waits for the in-flight request; overlapping operations are rejected");
        static IEnumerable<Mail> BrokenInput(Mail first) { yield return first; throw new InvalidOperationException("broken enumeration"); }
        try { await engine.ExecuteAsync(BrokenInput(mails[0]),"アーカイブ",null,null,CancellationToken.None); }
        catch(InvalidOperationException) {}
        var afterBadInput=await engine.ExecuteAsync([mails[3]],"アーカイブ",null,null,CancellationToken.None);
        if(afterBadInput.Items[0].State!="成功") throw new Exception("対象列挙失敗後に操作できません");
        Console.WriteLine("PASS: failed target enumeration does not lock future operations");
        Console.WriteLine($"Load test data: {root}");
    }
    sealed class BlockingGateway : IMailGateway
    {
        public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<Mail>> ListAsync(Account a,CancellationToken ct)=>throw new NotSupportedException();
        public Task<byte[]> RawAsync(Mail m,CancellationToken ct)=>throw new NotSupportedException();
        public async Task ChangeAsync(Mail m,string action,string? label,CancellationToken ct)
        { Started.TrySetResult(); await Release.Task; }
    }
    sealed class InlineProgress : IProgress<ProgressInfo>
    {
        public ProgressInfo? Last;
        public void Report(ProgressInfo value) => Last=value;
    }
}
