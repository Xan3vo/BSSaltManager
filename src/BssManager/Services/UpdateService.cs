using System.Windows;
using BssManager.Views;
using Velopack;
using Velopack.Sources;

namespace BssManager.Services;

/// <summary>
/// Keeps the installed app up to date from its GitHub releases.
///
/// Velopack installs to a per-user folder and stages updates there too, so
/// nothing here needs elevation of its own -- the download and file swap both
/// happen under %LocalAppData%. The one visible cost is that applying an update
/// relaunches the app, which re-triggers the UAC prompt this app already shows
/// on every start.
///
/// This does nothing when the app was not installed by Velopack -- running from
/// source, or from an unzipped folder, has no release feed to check against and
/// no install to replace. That keeps `dotnet run` quiet.
/// </summary>
public static class UpdateService
{
    private const string RepoUrl = "https://github.com/Xan3vo/BSSaltManager";

    /// <summary>
    /// Checks for a newer release, downloads it if there is one, and offers to
    /// restart into it. Runs entirely in the background and swallows its own
    /// failures: a missing network or an unreachable GitHub is not an error the
    /// user launched the app to hear about.
    /// </summary>
    public static async Task CheckAndPromptAsync()
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));

            // Not a Velopack install (source build, loose folder). Nothing to do.
            if (!mgr.IsInstalled) return;

            var update = await mgr.CheckForUpdatesAsync();
            if (update is null) return; // already current

            var version = update.TargetFullRelease.Version;
            Log.Write($"update available: {version}");

            await mgr.DownloadUpdatesAsync(update);

            // Ask on the UI thread; the download ran off it.
            var choice = await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageDialog.Show(
                    "Update available",
                    $"Version {version} is ready to install. Restart now to update?\n\n"
                        + "You can keep working and it will apply next time you launch.",
                    primary: "Restart now",
                    secondary: "Later"));

            if (choice == DialogChoice.Primary)
            {
                Log.Write($"applying update {version} and restarting");
                mgr.ApplyUpdatesAndRestart(update);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"update check failed: {ex.Message}");
        }
    }
}
