using System.Windows;
using System.Windows.Threading;
using Movewise.App.Services;
using Velopack;

namespace Movewise.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must run first: when the installer or updater starts Movewise to finish installing, this handles it and exits.
        VelopackApp.Build().Run();

        // One Movewise per user: two could deploy into the same tenant at once.
        using var single = new Mutex(true, @"Local\Movewise.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("Movewise is already open. Switch to it from the taskbar.", "Movewise", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DiagnosticLog.Prune();
        DiagnosticLog.Info($"Movewise {DiagnosticLog.Version} started on Windows {Environment.OSVersion.Version}.");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DiagnosticLog.Error("Movewise stopped unexpectedly.", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => DiagnosticLog.Error("A background task failed.", e.Exception);

        var app = new App();
        app.DispatcherUnhandledException += OnUnhandled;
        app.InitializeComponent();
        app.Run();
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLog.Error("Unexpected error.", e.Exception);
        MessageBox.Show(
            $"Something went wrong: {e.Exception.Message}\n\nMovewise will keep running. If this happens again, save a support log from the sidebar and send it to support.",
            "Movewise", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
