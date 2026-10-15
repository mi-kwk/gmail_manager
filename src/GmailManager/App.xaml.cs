using System.IO;
using System.Windows;

namespace GmailManager;

public partial class App : Application
{
    private Mutex? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke=e.Args.Length==2 && e.Args[0]=="--smoke-ui";
        try
        {
            if(!smoke)
            {
                instance=new Mutex(true,@"Local\GmailManager.Desktop",out var first);
                if(!first) { MessageBox.Show("Gmail Managerは既に起動しています。"); Shutdown(); return; }
            }
            var window=new MainWindow(smoke?Path.Combine(Path.GetFullPath(e.Args[1]),"data"):null);
            MainWindow=window;
            if(smoke)
            {
                window.ShowActivated=false;
                window.Left=-20000;
                window.Show();
                await window.SmokeAsync(Path.GetFullPath(e.Args[1]));
                Shutdown(0);
            }
            else window.Show();
        }
        catch(Exception ex)
        {
            if(smoke) File.WriteAllText(Path.Combine(Path.GetFullPath(e.Args[1]),"error.txt"),ex.ToString());
            else MessageBox.Show(ex.Message,"起動エラー");
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
