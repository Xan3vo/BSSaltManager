using System.Windows;
using System.Windows.Threading;
using BssManager.Services;
using BssManager.Views;

namespace BssManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureCreated();
        Log.Write("--- BSS Alt Manager started ---");

        // A crash here means an alt might be half-created. Show what happened
        // and write it down rather than vanishing silently.
        DispatcherUnhandledException += OnUnhandledException;
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
