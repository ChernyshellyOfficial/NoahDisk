using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace NoahDisk.Gui;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
    }

    void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true; // не даём приложению молча закрыться
        try { MessageBox.Show(e.Exception.ToString(), "NoahDisk — ошибка"); } catch { }
    }

    public static void Log(Exception? ex)
    {
        if (ex == null) return;
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.GetTempPath() })
        {
            try
            {
                File.AppendAllText(Path.Combine(dir, "NoahDisk-crash.log"),
                    $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\n{ex}\n\n");
                return;
            }
            catch { }
        }
    }
}
