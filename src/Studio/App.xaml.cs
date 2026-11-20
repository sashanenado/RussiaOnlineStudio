using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace RussiaOnlineStudio;

public partial class App : Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool createdNew;
        var mutexName = BuildInstanceMutexName("Studio");
        _instanceMutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);

        if (!createdNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            MessageBox.Show("Эта копия Studio уже запущена для данной папки Data. Переключись на открытое окно.",
                "Russia Online Studio", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("DispatcherUnhandledException", e.Exception);
        try
        {
            MessageBox.Show("Произошла критическая ошибка. Studio закроется, чтобы не продолжать работу в повреждённом состоянии.\n\nЖурнал: Data\\logs\\fatal.log\n\n" + e.Exception.Message,
                "Russia Online Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }

        // A dispatcher exception may leave controls and editor state inconsistent.
        // Close cleanly instead of pretending the process can safely continue.
        e.Handled = true;
        try { Dispatcher.BeginInvoke(new Action(() => Shutdown(-1))); }
        catch { Shutdown(-1); }
    }

    private static string BuildInstanceMutexName(string application)
    {
        var dataPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Data"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataPath)))[..16];
        return $"Local\\RussiaOnlineStudio.{application}.{hash}";
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
