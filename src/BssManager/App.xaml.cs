using System.Windows;
using System.Windows.Threading;
using BssManager.Services;
using BssManager.Views;
using Velopack;

namespace BssManager;

public partial class App : Application
{
    [STAThread]
    public static void Main()
    {
        // Must be the very first thing the app does. On a normal launch this
        // returns immediately; when the installer/updater invokes the exe with
        // its hook arguments, Velopack handles them here and exits before any
        // window is shown. Nothing runs above this line, ever.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();
        Log.Write("--- BSS Alt Manager started ---");

        // A crash here means an alt might be half-created. Show what happened
        // and write it down rather than vanishing silently.
        DispatcherUnhandledException += OnUnhandledException;

        // Look for a newer release in the background, once the window is up.
        // Never blocks startup and never throws into it -- a failed check just
        // means no update this run.
        _ = UpdateService.CheckAndPromptAsync();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write($"UNHANDLED: {e.Exception}");

        MessageDialog.Show("Something went wrong",
            $"{e.Exception.Message}\n\nDetails were written to {AppPaths.LogFile}",
            primary: "OK", kind: DialogKind.Danger);

        e.Handled = true;
    }
}
