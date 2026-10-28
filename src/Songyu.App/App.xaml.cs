using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Songyu.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception, "DispatcherUnhandled");
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        WriteCrash(e.ExceptionObject as Exception, "AppDomainUnhandled");
    }

    /// <summary>
    /// 崩溃日志直接落在可执行文件旁的 logs/ 里，绿色版不写用户目录。
    /// </summary>
    internal static void WriteCrash(Exception? ex, string stage)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var text = $"[{DateTimeOffset.Now:O}] {stage}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(dir, "crash.log"), text);
        }
        catch
        {
            // 记录失败时不能再抛，否则会递归崩溃。
        }
    }
}
