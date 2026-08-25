using System.IO;
namespace BssManager.Services;

/// <summary>Dead simple append-only log. Useful when a launch fails at 3am.</summary>
public static class Log
{
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        try
        {
            AppPaths.EnsureCreated();
            lock (Gate)
            {
                File.AppendAllText(AppPaths.LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
