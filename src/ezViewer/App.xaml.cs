using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using EzViewer.Core;
using EzViewer.Library;

namespace EzViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };

        var settings = AppSettings.Load();
        ThumbnailService.Instance.Start(Dispatcher);

        var startupPath = e.Args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
#if DEBUG
        int autotest = Array.IndexOf(e.Args, "--autotest");
        if (autotest >= 0) startupPath = null;
#endif
        var window = new MainWindow(settings, startupPath);
        MainWindow = window;
#if DEBUG
        if (autotest >= 0 && autotest + 1 < e.Args.Length)
        {
            var script = e.Args[autotest + 1];
            window.Loaded += (_, _) => DebugAutomation.Run(window, script);
        }
#endif
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show(MainWindow, "发生了未处理的错误：\n" + e.Exception.Message, "ezViewer", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    internal static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ezViewer");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + ex + Environment.NewLine + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }
}
