using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace RussiaOnlineStudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Data", "logs", "fatal.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, DateTime.Now + Environment.NewLine + args.ExceptionObject + Environment.NewLine);
            }
            catch { }
        };
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "logs", "errors.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, DateTime.Now + Environment.NewLine + e.Exception + Environment.NewLine + "----" + Environment.NewLine);
            MessageBox.Show("Возникла ошибка, но Studio записала её в Data\\logs\\errors.log и попробует продолжить работу.\n\n" + e.Exception.Message,
                "Russia Online Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
        e.Handled = true;
    }
}