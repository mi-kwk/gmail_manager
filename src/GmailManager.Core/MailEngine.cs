using MimeKit;
using System.Collections.Concurrent;
using System.Text;

namespace GmailManager.Core;

public sealed class MailEngine(Store store, IMailGateway gateway)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();
    private readonly SemaphoreSlim operationLock = new(1);
    private SemaphoreSlim Gate(string key) => locks.GetOrAdd(key, _ => new(1));
    public static IEnumerable<Mail> SenderMatches(IEnumerable<Mail> mails, string sender) =>
        mails.Where(m => m.Eligible && string.Equals(m.Sender, sender.Trim(), StringComparison.Ordinal));
    public bool IsProtected(Mail m) => store.Get<Backup>("backup", m.Key)?.Protected == true;
    public async Task ProtectAsync(Mail mail, CancellationToken ct = default)
    {
        var gate = Gate(mail.Key);
        await gate.WaitAsync(ct);
        try
        {
            var old = store.Get<Backup>("backup", mail.Key);
            store.Put("backup", mail.Key, old is null
                ? new Backup(mail, true, "保存待ち", null, null)
                : old with { Protected = true });
        }
        finally { gate.Release(); }
    }
    public async Task RegisterAsync(Mail mail, CancellationToken ct = default)
    {
        await ProtectAsync(mail, ct);
        await BackupAsync(mail, ct);
    }
    public async Task BackupAsync(Mail mail, CancellationToken ct = default)
    {
        var gate = Gate(mail.Key);
        await gate.WaitAsync(ct);
        try
        {
            var record = store.Get<Backup>("backup", mail.Key) ?? throw new InvalidOperationException("重要登録がありません");
            if(record.State == "保存完了" && File.Exists(record.Path)) return;
            store.Put("backup", mail.Key, record with { State = "保存中", Error = null });
            try
            {
                var bytes = await gateway.RawAsync(mail, ct);
                using var stream = new MemoryStream(bytes);
                using var mime = await MimeMessage.LoadAsync(stream, ct);
                var folder = Path.Combine(store.Root, "backups", mail.AccountId);
                Directory.CreateDirectory(folder);
                // Gmail IDs are opaque; derive a safe file name rather than trusting remote text.
                var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(mail.Key)));
                var path = Path.Combine(folder, name + ".eml");
                await File.WriteAllBytesAsync(path + ".tmp", bytes, ct);
                File.Move(path + ".tmp", path, true);
                store.Put("backup", mail.Key, record with { State = "保存完了", Path = path, Error = null });
            }
            catch(Exception ex)
            {
                store.Put("backup", mail.Key, record with { State = "失敗", Error = ex is OperationCanceledException ? "保存が中断されました" : ex.Message });
                throw;
            }
        }
        finally { gate.Release(); }
    }
    public async Task UnregisterAsync(Mail mail)
    {
        var gate = Gate(mail.Key); await gate.WaitAsync();
        try
        {
            var b = store.Get<Backup>("backup", mail.Key);
            if(b is not null) store.Put("backup", mail.Key, b with { Protected = false });
        }
        finally { gate.Release(); }
    }
    public async Task<Operation> ExecuteAsync(IEnumerable<Mail> mails, string action, string? label,
        IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if(!await operationLock.WaitAsync(0)) throw new InvalidOperationException("別の操作が実行中です");
        var op = new Operation(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, action,
            mails.DistinctBy(m => m.Key).Select(m => new OperationItem(m, "未処理")).ToList(), label);
        try
        {
            store.Put("operation", op.Id, op);
            foreach(var batch in Enumerable.Range(0, op.Items.Count).Chunk(50))
            {
                foreach(var index in batch)
                {
                    if(ct.IsCancellationRequested) break;
                    var item = op.Items[index]; var gate = Gate(item.Mail.Key);
                    try { await gate.WaitAsync(ct); }
                    catch(OperationCanceledException) { break; }
                    try
                    {
                        // This check shares a lock with registration and runs immediately before each request.
                        if(action == "ゴミ箱移動" && IsProtected(item.Mail))
                            op.Items[index] = item with { State = "保護除外" };
                        else if(action == "ゴミ箱移動" && !item.Mail.Eligible)
                            op.Items[index] = item with { State = "対象外" };
                        else
                        {
                            op.Items[index] = item with { State = "実行中" }; store.Put("operation", op.Id, op);
                            // Cancel stops unsent work. An already submitted request is allowed to finish.
                            await gateway.ChangeAsync(item.Mail, action, label, CancellationToken.None);
                            op.Items[index] = item with { State = "成功" };
                            var cached = store.Get<Mail>("mail", item.Mail.Key);
                            if(cached is not null)
                            {
                                var labels = cached.Labels.ToHashSet();
                                if(action == "ゴミ箱移動") { labels.Add("TRASH"); labels.Remove("INBOX"); }
                                if(action == "アーカイブ") labels.Remove("INBOX");
                                if(action == "ラベル") labels.Add(label!);
                                store.Put("mail", cached.Key, cached with { Labels = labels.ToArray() });
                            }
                        }
                    }
                    catch(Exception ex) { op.Items[index] = item with { State = "失敗", Error = ex.Message }; }
                    finally { gate.Release(); }
                    store.Put("operation", op.Id, op);
                    progress?.Report(new(op.Items.Count(i => i.State != "未処理"), op.Items.Count, "分割処理中"));
                }
                if(ct.IsCancellationRequested) break;
                await Task.Yield();
            }
            store.Put("operation", op.Id, op);
            return op;
        }
        finally { operationLock.Release(); }
    }
    private static string HtmlAsText(string html)
    {
        try
        {
            var text=System.Text.RegularExpressions.Regex.Replace(html,@"<(script|style)\b[^>]*>[\s\S]*?</\1\s*>","",System.Text.RegularExpressions.RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1));
            text=System.Text.RegularExpressions.Regex.Replace(text,@"<[^>]+>"," ",System.Text.RegularExpressions.RegexOptions.None,TimeSpan.FromSeconds(1));
            return System.Net.WebUtility.HtmlDecode(text);
        }
        catch(System.Text.RegularExpressions.RegexMatchTimeoutException) { return html; }
    }
    public static async Task<(string Body, List<(string Name, byte[] Data)> Attachments)> ReadBackupAsync(Backup backup)
    {
        if(backup.State != "保存完了" || backup.Path is null) throw new InvalidOperationException("バックアップ未完了です");
        using var stream = File.OpenRead(backup.Path);
        using var mime = await MimeMessage.LoadAsync(stream);
        var files = new List<(string, byte[])>();
        foreach(var part in mime.BodyParts)
        {
            if(part is MimePart p && (p.IsAttachment || !string.IsNullOrEmpty(p.FileName) || p.ContentId is not null))
            {
                using var output = new MemoryStream(); await p.Content!.DecodeToAsync(output);
                files.Add((Path.GetFileName(p.FileName ?? p.ContentId ?? "attachment"), output.ToArray()));
            }
            else if(part is MessagePart mp && mp.IsAttachment)
            {
                using var output = new MemoryStream(); await mp.Message!.WriteToAsync(output);
                files.Add((Path.GetFileName(mp.ContentDisposition?.FileName ?? "message.eml"), output.ToArray()));
            }
        }
        return (mime.TextBody ?? (mime.HtmlBody is null ? "本文なし" : HtmlAsText(mime.HtmlBody)), files);
    }
}
