using GmailManager.Core;
using System.Text;

var root=Path.Combine(Path.GetTempPath(),"GmailManager.Tests",Guid.NewGuid().ToString("N"));
var store=new Store(root);
var gateway=new FakeGateway();
var engine=new MailEngine(store,gateway);
int checks=0;
void Check(bool value,string name) { if(!value) throw new Exception("FAIL: "+name); Console.WriteLine("PASS: "+name); checks++; }
Mail M(string account,string id,string sender="sender@example.com",params string[] labels) => new(account,id,sender,"件名 "+id,DateTimeOffset.Now,labels);
var a=new Account("account-a","a@example.com","共通");
var b=new Account("account-b","b@example.com","共通");
store.Put("account",a.Id,a); store.Put("account",b.Id,b);
Check(new Store(root).All<Account>("account").Count==2,"SQLite preserves duplicate display names and distinct account IDs");
var m1=M(a.Id,"same"); var m2=M(b.Id,"same"); var spam=M(a.Id,"spam","sender@example.com","SPAM");
var trash=M(a.Id,"trash","sender@example.com","TRASH");
var archived=M(a.Id,"archived");
var inbox=M(b.Id,"inbox","sender@example.com","INBOX","UNREAD");
var other=M(a.Id,"other","sender+other@example.com");
var list=new[]{m1,m2,spam,trash,archived,inbox,other};
store.ReplaceMail(a,list.Where(m=>m.AccountId==a.Id).ToList());
store.ReplaceMail(b,list.Where(m=>m.AccountId==b.Id).ToList());
Check(store.All<Mail>("mail").Count==7,"same Gmail ID remains separate across accounts");
Check(MailEngine.SenderMatches(list,"sender@example.com").Count()==4,"exact sender match includes inbox and archive; excludes spam, trash and similar address");

gateway.RawBlock=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
var registration=engine.RegisterAsync(m1);
Check(engine.IsProtected(m1),"protection is persisted before backup completes");
Check(store.Get<Backup>("backup",m1.Key)?.State=="保存中","backup state tracked independently");
gateway.RawBlock.SetException(new IOException("simulated offline"));
try { await registration; } catch(IOException) {}
Check(engine.IsProtected(m1) && store.Get<Backup>("backup",m1.Key)?.State=="失敗","failed backup retains deletion protection");
var op=await engine.ExecuteAsync(new[]{m1,m2},"ゴミ箱移動",null,null,CancellationToken.None);
Check(op.Items[0].State=="保護除外" && op.Items[1].State=="成功","protection scoped to account and rechecked before deletion");

gateway.RawBlock=null;
await engine.BackupAsync(m1);
var saved=store.Get<Backup>("backup",m1.Key)!;
gateway.Offline=true;
var content=await MailEngine.ReadBackupAsync(saved);
Check(content.Body.Contains("offline body") && content.Attachments.Single().Data.SequenceEqual(Encoding.UTF8.GetBytes("attachment bytes")),"offline local body and attachment survive remote deletion");
await engine.UnregisterAsync(m1);
Check(!engine.IsProtected(m1) && File.Exists(saved.Path),"unregister retains backup file");
gateway.Offline=false;

var cancellation=new CancellationTokenSource();
gateway.AfterChange=()=>cancellation.Cancel();
var many=Enumerable.Range(0,120).Select(i=>M(a.Id,"bulk"+i)).ToArray();
op=await engine.ExecuteAsync(many,"ゴミ箱移動",null,null,cancellation.Token);
Check(op.Items.Count(i=>i.State=="成功")==1 && op.Items.Count(i=>i.State=="未処理")==119,"cancel stops unsent items and does not restore completed work");
gateway.AfterChange=null;
gateway.FailId="bulk2";
op=await engine.ExecuteAsync(many.Take(4),"ゴミ箱移動",null,null,CancellationToken.None);
Check(op.Items.Count(i=>i.State=="失敗")==1 && op.Items.Count(i=>i.State=="成功")==3,"partial failure isolates failed item");
gateway.FailId=null;
var retried=await engine.ExecuteAsync(op.Items.Where(i=>i.State=="失敗").Select(i=>i.Mail),"ゴミ箱移動",null,null,CancellationToken.None);
Check(retried.Items.Count==1 && retried.Items[0].State=="成功","retry targets only failures");
gateway.AfterChange=()=>store.Put("backup",many[1].Key,new Backup(many[1],true,"保存待ち",null,null));
op=await engine.ExecuteAsync(many.Take(2),"ゴミ箱移動",null,null,CancellationToken.None);
Check(op.Items[1].State=="保護除外","protection added after confirmation excludes upcoming deletion");
gateway.AfterChange=null;

store.Put("backup",m2.Key,new Backup(m2,true,"保存中",null,null));
var interrupted=new Operation("interrupted",DateTimeOffset.Now,"ゴミ箱移動",[new(m2,"実行中")]);
store.Put("operation",interrupted.Id,interrupted);
var restart=new Store(root);
Check(restart.Get<Backup>("backup",m2.Key) is { Protected:true,State:"失敗" },"interrupted backup stays protected after restart");
Check(restart.Get<Operation>("operation","interrupted")!.Items[0].State=="要確認","interrupted in-flight request is not silently retried");
var liveContent=await MailEngine.ReadMessageAsync(await gateway.RawAsync(m1,CancellationToken.None));
Check(liveContent.Body.Contains("offline body") && liveContent.Attachments.Count==1,"ordinary unregistered messages can be parsed for preview");
var htmlContent=await MailEngine.ReadMessageAsync(Encoding.UTF8.GetBytes("From: sender@example.com\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>Preview &amp; body</p><script>hidden_script()</script>"));
Check(htmlContent.Body.Contains("Preview & body") && !htmlContent.Body.Contains("hidden_script"),"HTML preview is decoded as inert text without scripts");
Console.WriteLine($"{checks} checks passed. Test data: {root}");

sealed class FakeGateway : IMailGateway
{
    public TaskCompletionSource<byte[]>? RawBlock;
    public bool Offline;
    public string? FailId;
    public Action? AfterChange;
    public Task<IReadOnlyList<Mail>> ListAsync(Account a,CancellationToken ct) => Task.FromResult<IReadOnlyList<Mail>>([]);
    public Task<byte[]> RawAsync(Mail m,CancellationToken ct)
    {
        if(Offline) throw new IOException("offline");
        if(RawBlock is not null) return RawBlock.Task;
        const string eml="From: sender@example.com\r\nTo: a@example.com\r\nSubject: saved\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=test\r\n\r\n--test\r\nContent-Type: text/plain; charset=utf-8\r\n\r\noffline body\r\n--test\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=test.txt\r\nContent-Transfer-Encoding: base64\r\n\r\nYXR0YWNobWVudCBieXRlcw==\r\n--test--\r\n";
        return Task.FromResult(Encoding.UTF8.GetBytes(eml));
    }
    public Task ChangeAsync(Mail m,string action,string? label,CancellationToken ct)
    {
        if(m.Id==FailId) throw new IOException("simulated failure");
        AfterChange?.Invoke(); return Task.CompletedTask;
    }
}
