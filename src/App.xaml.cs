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
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => { if (sender is Window window) window.Icon = Ui.AppIcon; }));
        if (Array.IndexOf(e.Args, "--ocr-worker") == 0) { Shutdown(OcrService.RunWorker(e.Args)); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write(args.Exception);
            MessageBox.Show(args.Exception.Message, "轻截 · 操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
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
    protected override void OnExit(ExitEventArgs e) { OcrService.CancelActive(); _instance?.Dispose(); base.OnExit(e); }
}
