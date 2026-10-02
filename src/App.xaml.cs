using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace QuickCapture;

public partial class App : Application
{
    private Mutex? _instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            MessageBox.Show(args.Exception.Message, "轻截 · 操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        if (Array.IndexOf(e.Args, "--self-test") >= 0)
        {
            int code = await SelfTest.RunAsync(e.Args);
            Shutdown(code);
            return;
        }
        if (Array.IndexOf(e.Args, "--text-demo") >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await AcceptanceArtifacts.RunDemoAsync(true);
            return;
        }
        _instance = new Mutex(true, "Local\\QuickCapture.Personal.x64", out bool created);
        if (!created)
        {
            MessageBox.Show("轻截已在运行，请从系统托盘打开。", "轻截");
            Shutdown();
            return;
        }
        try { new MainWindow().Show(); }
        catch (Exception ex) { ErrorLog.Write(ex); MessageBox.Show(ex.Message, "轻截启动失败"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}

internal static class ErrorLog
{
    public static void Write(Exception ex)
    {
        try { Directory.CreateDirectory(Paths.Data); File.AppendAllText(Path.Combine(Paths.Data, "errors.log"), $"{DateTime.Now:O} {ex}\n"); }
        catch { /* Error reporting must not mask the original failure. */ }
    }
}
