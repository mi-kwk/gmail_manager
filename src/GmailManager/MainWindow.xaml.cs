using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GmailManager.Core;
using Microsoft.Win32;

namespace GmailManager;

public partial class MainWindow : Window
{
    private readonly Store store;
    private readonly GmailGateway gateway;
    private readonly MailEngine engine;
    private CancellationTokenSource? work;
    private bool refreshing;
    public MainWindow(string? dataRoot = null)
    {
        InitializeComponent();
        var root = dataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GmailManager");
        store = new Store(root);
        gateway = new GmailGateway(store,Path.Combine(root,"client_secret.json"));
        engine = new MailEngine(store,gateway);
        Refresh();
        Closing += (_,e) => { if(work is not null) { e.Cancel = true; MessageBox.Show("処理中です。キャンセル後、結果を確認して終了してください。"); } else gateway.Dispose(); };
    }
    private string AccountName(string id) => store.Get<Account>("account",id)?.Caption ?? id;
    private record MailRow(Mail Value, string 元アカウント, string 送信元, string 件名, string 受信日時, string ラベル, string 削除保護);
    private record BackupRow(Backup Value, string 元アカウント, string 送信元, string 件名, string 保存状態, string 保護, string 保存先, string エラー);
    private record SenderRow(string 送信元, int 全体件数, string アカウント別件数);
    private record AccountRow(Account Value, string 表示名, string メールアドレス, string 内部識別子);
    private record HistoryRow(Operation Value, string 実行日時, string 操作, int 成功, int 失敗, int 保護除外, int 未処理, int 要確認);
    private void Bind(DataGrid grid, object rows)
    {

        grid.AutoGeneratingColumn -= HideValue;
        grid.AutoGeneratingColumn += HideValue;
        grid.ItemsSource = (System.Collections.IEnumerable)rows;
    }
    private void HideValue(object? sender, DataGridAutoGeneratingColumnEventArgs e) { if(e.PropertyName == "Value") e.Cancel = true; }
    private bool Contains(string value,string term) => value.Contains(term.Trim(),StringComparison.OrdinalIgnoreCase);
    private void Refresh()
    {
        if(MailGrid is null || store is null || refreshing) return;
        refreshing = true;
        try
        {
            var accounts = store.All<Account>("account");
            var chosen = (AccountFilter.SelectedItem as Account)?.Id ?? "";
            AccountFilter.ItemsSource = new[]{ new Account("","","全アカウント") }.Concat(accounts).ToList();
            AccountFilter.SelectedItem = ((IEnumerable<Account>)AccountFilter.ItemsSource).FirstOrDefault(a => a.Id == chosen);
            var mails = store.All<Mail>("mail").Where(m => m.Eligible).ToList();
            Bind(MailGrid,mails.Where(m => (chosen == "" || m.AccountId == chosen) &&
                Contains(m.Subject+" "+m.Sender+" "+AccountName(m.AccountId),MailSearch.Text))
                .OrderByDescending(m => m.Date).Select(m => new MailRow(m,AccountName(m.AccountId),m.Sender,m.Subject,m.Date.LocalDateTime.ToString("yyyy/MM/dd HH:mm"),string.Join(", ",m.Labels),engine.IsProtected(m)?"保護中":"")).ToList());
            Bind(SenderGrid,mails.GroupBy(m=>m.Sender,StringComparer.Ordinal).OrderByDescending(g=>g.Count())
                .Select(g=>new SenderRow(g.Key,g.Count(),string.Join(" / ",g.GroupBy(m=>m.AccountId).Select(a=>$"{AccountName(a.Key)}: {a.Count()}")))).ToList());
            BackupRow Row(Backup b) => new(b,AccountName(b.Mail.AccountId),b.Mail.Sender,b.Mail.Subject,b.State,b.Protected?"保護中":"解除済み",b.Path??"",b.Error??"");
            var backups = store.All<Backup>("backup");
            Bind(VaultGrid,backups.Where(b=>b.Protected && Contains(AccountName(b.Mail.AccountId),VaultAccount.Text) &&
                Contains(b.Mail.Sender,VaultSender.Text) && Contains(b.Mail.Subject,VaultSubject.Text)).Select(Row).ToList());
            Bind(BackupGrid,backups.Select(Row).ToList());
            Bind(AccountsGrid,accounts.Select(a=>new AccountRow(a,a.DisplayName,a.Email,a.Id)).ToList());
            Bind(HistoryGrid,store.All<Operation>("operation").OrderByDescending(o=>o.Date).Select(o=>new HistoryRow(o,o.Date.LocalDateTime.ToString("yyyy/MM/dd HH:mm:ss"),o.Kind,
                o.Items.Count(i=>i.State=="成功"),o.Items.Count(i=>i.State=="失敗"),o.Items.Count(i=>i.State=="保護除外"),o.Items.Count(i=>i.State=="未処理"),o.Items.Count(i=>i.State=="要確認"))).ToList());
        }
        finally { refreshing = false; }
    }
    private async Task Run(Func<CancellationToken,Task> action)
    {
        if(work is not null) { MessageBox.Show("別の処理が実行中です。"); return; }
        work = new(); ConnectButton.IsEnabled = SyncButton.IsEnabled = false;
        try { await action(work.Token); }
        catch(OperationCanceledException) { Status.Text = "キャンセルしました。処理済み分は維持されます。"; }
        catch(Exception ex) { Status.Text = "処理に失敗しました"; MessageBox.Show(ex.Message,"エラー"); }
        finally { work.Dispose(); work = null; ConnectButton.IsEnabled = SyncButton.IsEnabled = true; Refresh(); }
    }
    private async Task Sync(CancellationToken ct)
    {
        foreach(var account in store.All<Account>("account"))
        {
            Status.Text = $"{account.Caption} の全期間メールを取得中…";
            var mails = await gateway.ListAsync(account,ct);
            store.ReplaceMail(account,mails);
            Refresh();
        }
        Status.Text = "同期が完了しました。表示件数はこの同期時点のものです。";
    }
    private async void Connect_Click(object s,RoutedEventArgs e) => await Run(async ct =>
    {
        var path = Path.Combine(store.Root,"client_secret.json");
        if(!File.Exists(path))
        {
            var picker = new OpenFileDialog { Filter = "Google OAuthデスクトップアプリ設定 (*.json)|*.json", Title = "ダウンロードしたOAuthクライアントJSONを選択" };
            if(picker.ShowDialog()!=true) return;
            File.Copy(picker.FileName,path,true);
        }
        Status.Text="ブラウザーでGoogle連携を許可してください";
        await gateway.ConnectAsync(DisplayName.Text,ct);
        Status.Text="連携しました。「全アカウントを同期」でメールを取得できます。";
    });
    private async void Sync_Click(object s,RoutedEventArgs e) => await Run(Sync);
    private void Cancel_Click(object s,RoutedEventArgs e) { work?.Cancel(); Status.Text="キャンセル要求済み。実行中のリクエスト完了後に停止します。"; }
    private void Folder_Click(object s,RoutedEventArgs e) => Process.Start(new ProcessStartInfo(store.Root){UseShellExecute=true});
    private IEnumerable<Mail> SelectedMail() => MailGrid.SelectedItems.OfType<MailRow>().Select(r=>r.Value).ToArray();
    private async void Register_Click(object s,RoutedEventArgs e)
    {
        var selected = SelectedMail().ToArray();
        await Run(async ct =>
        {
            foreach(var mail in selected) await engine.ProtectAsync(mail,ct);
            Refresh();
            int failed=0;
            foreach(var mail in selected)
            {
                ct.ThrowIfCancellationRequested();
                try { await engine.BackupAsync(mail,ct); }
                catch(OperationCanceledException) { throw; }
                catch { failed++; }
            }
            Status.Text=$"重要登録しました。保存完了: {selected.Length-failed}件 / 保存失敗: {failed}件。失敗中も保護を維持します。";
        });
    }
    private bool Confirm(IEnumerable<Mail> list,string action)
    {
        var mails = list.ToArray();
        if(mails.Length==0) { MessageBox.Show("対象がありません"); return false; }
        var protect = action=="ゴミ箱移動" ? mails.Count(engine.IsProtected) : 0;
        var breakdown = string.Join("\n",mails.GroupBy(m=>m.AccountId).Select(g=>$"{AccountName(g.Key)}: {g.Count()}件"));
        return MessageBox.Show($"{action}: {mails.Length}件\n保護除外予定: {protect}件\n{breakdown}\n\n未処理分をキャンセルできます。処理済み分は自動復元しません。\n実行しますか？","操作前確認",MessageBoxButton.OKCancel,MessageBoxImage.Question)==MessageBoxResult.OK;
    }
    private async Task Execute(IEnumerable<Mail> list,string action,CancellationToken ct,string? label=null)
    {
        var op = await engine.ExecuteAsync(list,action,label,new Progress<ProgressInfo>(p =>
        { Progress.Value=p.Total==0?0:100.0*p.Completed/p.Total; Status.Text=$"{p.Message} {p.Completed}/{p.Total}"; }),ct);
        Status.Text=string.Join(" / ",op.Items.GroupBy(i=>i.State).Select(g=>$"{g.Key}: {g.Count()}件"));
        Refresh();
    }
    private async void TrashSelected_Click(object s,RoutedEventArgs e)
    { var mails=SelectedMail().ToArray(); if(Confirm(mails,"ゴミ箱移動")) await Run(ct=>Execute(mails,"ゴミ箱移動",ct)); }
    private async void Archive_Click(object s,RoutedEventArgs e)
    { var mails=SelectedMail().ToArray(); if(Confirm(mails,"アーカイブ")) await Run(ct=>Execute(mails,"アーカイブ",ct)); }
    private async void TrashSender_Click(object s,RoutedEventArgs e)
    {
        var sender=SenderInput.Text.Trim();
        if(!MimeKit.MailboxAddress.TryParse(sender,out var address) || address.Address!=sender) { MessageBox.Show("送信元メールアドレスを入力してください"); return; }
        await Run(async ct =>
        {
            await Sync(ct);
            var mails=MailEngine.SenderMatches(store.All<Mail>("mail"),sender).ToArray();
            if(Confirm(mails,"ゴミ箱移動")) await Execute(mails,"ゴミ箱移動",ct);
        });
    }
    private async void Label_Click(object s,RoutedEventArgs e)
    {
        var mails=SelectedMail().ToArray();
        if(mails.Length==0 || mails.Select(m=>m.AccountId).Distinct().Count()!=1)
        { MessageBox.Show("ラベル分類は同じアカウントのメールを選択してください"); return; }
        await Run(async ct =>
        {
            var account=store.Get<Account>("account",mails[0].AccountId)!;
            var labels=(await gateway.LabelsAsync(account,ct)).Where(l=>l.Type=="user").ToList();
            var box=new ComboBox { ItemsSource=labels,DisplayMemberPath="Name",Margin=new Thickness(16),MinWidth=300 };
            var ok=new Button { Content="このラベルで分類",Margin=new Thickness(16) };
            var panel=new StackPanel(); panel.Children.Add(box); panel.Children.Add(ok);
            var dialog=new Window { Owner=this,Title=account.Caption,Content=panel,SizeToContent=SizeToContent.WidthAndHeight,WindowStartupLocation=WindowStartupLocation.CenterOwner };
            ok.Click+=(_,_)=> { if(box.SelectedItem is not null) dialog.DialogResult=true; };
            if(dialog.ShowDialog()==true && box.SelectedItem is Google.Apis.Gmail.v1.Data.Label label && Confirm(mails,"ラベル"))
                await Execute(mails,"ラベル",ct,label.Id);
        });
    }
    private void Sender_Changed(object s,SelectionChangedEventArgs e) { if(SenderGrid.SelectedItem is SenderRow row) SenderInput.Text=row.送信元; }
    private void Rename_Click(object s,RoutedEventArgs e)
    {
        if(AccountsGrid.SelectedItem is AccountRow row && !string.IsNullOrWhiteSpace(DisplayName.Text))
        { store.Put("account",row.Value.Id,row.Value with { DisplayName=DisplayName.Text.Trim() }); Refresh(); Status.Text="アプリ内表示名を保存しました"; }
    }
    private async void Unregister_Click(object s,RoutedEventArgs e)
    {
        if(VaultGrid.SelectedItem is BackupRow row && MessageBox.Show("削除保護を解除します。バックアップは保持されます。","重要解除",MessageBoxButton.OKCancel)==MessageBoxResult.OK)
            await Run(async _=> { await engine.UnregisterAsync(row.Value.Mail); Status.Text="重要解除しました。バックアップは保持しています。"; });
    }
    private async Task OpenBackup(Backup? backup)
    {
        if(backup is null) return;
        try
        {
            var (body,files)=await MailEngine.ReadBackupAsync(backup);
            var panel=new DockPanel { Margin=new Thickness(16) };
            var attachments=new WrapPanel();
            foreach(var attachment in files)
            {
                var button=new Button { Content="添付を保存: "+attachment.Name };
                button.Click+=(_,_) =>
                {
                    var picker=new SaveFileDialog { FileName=Path.GetFileName(attachment.Name),Filter="すべてのファイル|*.*" };
                    if(picker.ShowDialog()==true)
                        try { File.WriteAllBytes(picker.FileName,attachment.Data); } catch(Exception ex) { MessageBox.Show(ex.Message); }
                };
                attachments.Children.Add(button);
            }
            DockPanel.SetDock(attachments,Dock.Bottom); panel.Children.Add(attachments);
            // Render as plain text: no remote images, scripts or network dependency.
            panel.Children.Add(new TextBox { Text=body,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto });
            new Window { Owner=this,Title=backup.Mail.Subject+" — 保存済み",Width=900,Height=650,Content=panel }.ShowDialog();
        }
        catch(Exception ex) { MessageBox.Show(ex.Message,"バックアップ閲覧"); }
    }
    private async void OpenVault_Click(object s,RoutedEventArgs e) => await OpenBackup((VaultGrid.SelectedItem as BackupRow)?.Value);
    private async void OpenBackup_Click(object s,RoutedEventArgs e) => await OpenBackup((BackupGrid.SelectedItem as BackupRow)?.Value);
    private async void RetryBackup_Click(object s,RoutedEventArgs e)
    { if(VaultGrid.SelectedItem is BackupRow row) await Run(ct=>engine.BackupAsync(row.Value.Mail,ct)); }
    private async void RetryAllBackup_Click(object s,RoutedEventArgs e)
    { if(BackupGrid.SelectedItem is BackupRow row) await Run(ct=>engine.BackupAsync(row.Value.Mail,ct)); }
    private void HistoryDetails_Click(object s,RoutedEventArgs e)
    {
        if(HistoryGrid.SelectedItem is not HistoryRow row) return;
        var grid=new DataGrid { AutoGenerateColumns=true,IsReadOnly=true,ItemsSource=row.Value.Items.Select(i=>new { 元アカウント=AccountName(i.Mail.AccountId),i.Mail.Id,件名=i.Mail.Subject,状態=i.State,エラー=i.Error }).ToList() };
        new Window { Owner=this,Title="処理詳細",Width=1000,Height=600,Content=grid }.ShowDialog();
    }
    private async void RetryOperation_Click(object s,RoutedEventArgs e)
    {
        if(HistoryGrid.SelectedItem is not HistoryRow row) return;

        var mails=row.Value.Items.Where(i=>i.State=="失敗").Select(i=>i.Mail).ToArray();
        if(Confirm(mails,row.Value.Kind)) await Run(ct=>Execute(mails,row.Value.Kind,ct,row.Value.Label));
    }
    public async Task SmokeAsync(string folder)
    {
        store.Put("account","smoke-a",new Account("smoke-a","first@example.com","仕事用"));
        store.Put("account","smoke-b",new Account("smoke-b","second@example.com","仕事用"));
        var a=new Mail("smoke-a","1","sender@example.com","重要書類 — 保護済み",DateTimeOffset.Now,["INBOX"]);
        var b=new Mail("smoke-b","1","sender@example.com","アーカイブ済みメール",DateTimeOffset.Now,[]);
        store.ReplaceMail(store.Get<Account>("account","smoke-a")!,[a]);
        store.ReplaceMail(store.Get<Account>("account","smoke-b")!,[b]);
        await engine.ProtectAsync(a);
        Refresh();
        Directory.CreateDirectory(folder);
        foreach(TabItem tab in Tabs.Items)
        {
            Tabs.SelectedItem=tab;
            UpdateLayout();
            await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(this);
            var png=new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(folder,$"tab-{Tabs.SelectedIndex}.png"));
            png.Save(file);
        }
        if(MailGrid.Items.Count!=2 || SenderGrid.Items.Count!=1 || VaultGrid.Items.Count!=1 || AccountsGrid.Items.Count!=2)
            throw new InvalidOperationException("UIの統合件数が不正です");
        if(MailGrid.Columns.Any(c=>c.Header?.ToString()=="Value")) throw new InvalidOperationException("内部値の列が表示されています");
        VaultSender.Text="no-match";
        if(VaultGrid.Items.Count!=0) throw new InvalidOperationException("保管庫検索が不正です");
        VaultSender.Text="";
        File.WriteAllText(Path.Combine(folder,"result.txt"),"PASS: 6 tabs rendered; duplicate accounts; combined mail counts; vault filter; internal columns hidden.");
    }
    private void Filter_Changed(object s,TextChangedEventArgs e) => Refresh();
    private void AccountFilter_Changed(object s,SelectionChangedEventArgs e) { if(!refreshing) Refresh(); }
    private void Tabs_SelectionChanged(object s,SelectionChangedEventArgs e) { if(e.Source==Tabs) Refresh(); }
}
