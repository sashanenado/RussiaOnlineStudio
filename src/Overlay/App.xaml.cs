using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;

namespace RussiaOnlineOverlay;

public partial class App : Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool createdNew;
        var mutexName = BuildInstanceMutexName();
        _instanceMutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);

        if (!createdNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            MessageBox.Show("Overlay уже запущен для этой папки Data. Используй открытое окно управления.",
                "Russia Online Overlay", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("DispatcherUnhandledException", args.Exception);
            try
            {
                MessageBox.Show("Произошла критическая ошибка. Overlay будет закрыт.\n\nЖурнал: Data\\logs\\fatal.log\n\n" + args.Exception.Message,
                    "Russia Online Overlay", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            args.Handled = true;
            try { Dispatcher.BeginInvoke(new Action(() => Shutdown(-1))); }
            catch { Shutdown(-1); }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain.UnhandledException", args.ExceptionObject?.ToString() ?? "Unknown fatal exception");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _instanceMutex?.ReleaseMutex(); }
        catch (ApplicationException) { }
        finally
        {
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        }
        base.OnExit(e);
    }

    private static string BuildInstanceMutexName()
    {
        var dataPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Data"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataPath)))[..16];
        return $"Local\\RussiaOnlineStudio.Overlay.{hash}";
    }

    private static void WriteCrashLog(string source, object details)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "logs", "fatal.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{details}{Environment.NewLine}---{Environment.NewLine}");
        }
        catch { }
    }
}
