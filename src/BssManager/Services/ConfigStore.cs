using System.IO;
using System.Text.Json;
using BssManager.Models;

namespace BssManager.Services;

public class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public AppConfig Load()
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(AppPaths.ConfigFile)) return new AppConfig();

        try
        {
            var json = File.ReadAllText(AppPaths.ConfigFile);
            return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            // A corrupt config should not stop the app from opening -- keep the
            // bad file around so nothing is silently destroyed.
            Log.Write($"config load failed, starting fresh: {ex.Message}");
            var backup = AppPaths.ConfigFile + $".broken-{DateTime.Now:yyyyMMddHHmmss}";
            try { File.Move(AppPaths.ConfigFile, backup); } catch { /* best effort */ }
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        AppPaths.EnsureCreated();
        var json = JsonSerializer.Serialize(config, Options);
        var tmp = AppPaths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
    }
}
