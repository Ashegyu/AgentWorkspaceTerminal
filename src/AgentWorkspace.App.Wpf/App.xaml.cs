using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AgentWorkspace.App.Wpf;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(
        AppContext.BaseDirectory, "agentworkspace-crash.log");

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrash("Dispatcher.UnhandledException", args.Exception);
            args.Handled = false;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    private static void WriteCrash(string kind, Exception? ex)
    {
        try
        {
            File.AppendAllText(
                CrashLogPath,
                $"[{DateTimeOffset.UtcNow:O}] {kind}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // last-ditch — there is nothing useful left to do
        }
    }
}
