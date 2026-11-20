using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ImageOptim.Win;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) WriteStartupError(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteStartupError(e.Exception);
            e.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string? log = WriteStartupError(e.Exception);
        try
        {
            MessageBox.Show(
                "程序启动或运行时发生错误。\n\n" + e.Exception.Message +
                (log is null ? "" : "\n\n详细日志：" + log),
                "ImageOptim 启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* Logging should never create a second fatal exception. */ }
        e.Handled = true;
        Shutdown(1);
    }

    private static string? WriteStartupError(Exception exception)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "ImageOptimWin");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "startup-error.log");
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:O}]\n{exception}\n\n", Encoding.UTF8);
            return path;
        }
        catch { return null; }
    }
}
