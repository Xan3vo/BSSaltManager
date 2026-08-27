using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.ServiceProcess;
using BssManager.Models;
using Microsoft.Win32;

namespace BssManager.Services;

/// <summary>
/// Everything about whether this machine can actually host multiple concurrent
/// sessions right now.
///
/// This exists because the single most common failure in this whole workflow is
/// silent: Windows updates termsrv.dll, rdpwrap.ini has no entry for the new
/// build, and multi-session simply stops working. mstsc then fails with a
/// generic error that tells the user nothing. Surfacing it by name is the point.
/// </summary>
public class RdpWrapService
{
    private const string TermServiceParamsKey = @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";
    private const string TerminalServerKey = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string TsClientKey = @"Software\Microsoft\Terminal Server Client";

    public string? InstallDirectory => ResolveInstallDirectory();

    /// <summary>
    /// The termsrv.dll version rdpwrap.ini is keyed on.
    ///
    /// Two traps here, both verified against this machine:
    ///
    /// 1. Use the numeric parts, not the FileVersion string. They disagree on
    ///    Windows system binaries -- this box reports parts 10.0.26100.8972 and
    ///    string 10.0.26100.8115. RDP Wrapper's own updater reads the version
    ///    with VBScript's FileSystemObject.GetFileVersion, which returns the
    ///    fixed-info value, so the numeric parts are what its ini sections match.
    ///
    /// 2. This depends on app.manifest declaring supportedOS for Windows 10+.
    ///    Without it Windows applies a compatibility shim and reports 6.2.x for
    ///    system files, which matches no ini section and would make this check
    ///    fail permanently for a reason nobody would ever guess.
    /// </summary>
    public string TermSrvVersion
    {
        get
        {
            try
            {
                var path = Path.Combine(Environment.SystemDirectory, "termsrv.dll");
                var v = FileVersionInfo.GetVersionInfo(path);
                return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}.{v.FilePrivatePart}";
            }
            catch
            {
                return "unknown";
            }
        }
    }

    public List<HealthCheck> RunAll()
    {
        return new List<HealthCheck>
        {
            CheckInstalled(),
            CheckWrapperHooked(),
            CheckServiceRunning(),
            CheckIniSupportsBuild(),
            CheckRdpEnabled(),
            CheckMultipleSessionsPerUser(),
            CheckSuppressWhenMinimized(),
            CheckListener()
        };
    }

    // ------------------------------------------------------------------ checks

    private HealthCheck CheckInstalled()
    {
        var dir = ResolveInstallDirectory();
        var dll = dir is null ? null : Path.Combine(dir, "rdpwrap.dll");
        var ok = dll is not null && File.Exists(dll);

        return new HealthCheck
        {
            Name = "RDP Wrapper installed",
            State = ok ? HealthState.Ok : HealthState.Failed,
            Detail = ok ? dir! : "rdpwrap.dll not found",
            Consequence = "Without RDP Wrapper, Windows allows only one interactive session, so alts cannot run alongside your own desktop.",
            Fix = ok ? FixAction.None : FixAction.InstallRdpWrap,
            FixLabel = "Get RDP Wrapper"
        };
    }

    private HealthCheck CheckWrapperHooked()
    {
        var serviceDll = Registry.LocalMachine.OpenSubKey(TermServiceParamsKey)?
            .GetValue("ServiceDll") as string ?? "";
        var hooked = serviceDll.Contains("rdpwrap.dll", StringComparison.OrdinalIgnoreCase);

        return new HealthCheck
        {
            Name = "TermService points at the wrapper",
            State = hooked ? HealthState.Ok : HealthState.Failed,
            Detail = string.IsNullOrEmpty(serviceDll) ? "ServiceDll not set" : serviceDll,
            Consequence = "Terminal Services is loading the stock DLL, so the wrapper is inert even if installed.",
            Fix = hooked ? FixAction.None : FixAction.InstallRdpWrap,
            FixLabel = "Reinstall wrapper"
        };
    }

    private HealthCheck CheckServiceRunning()
    {
        var (running, detail) = QueryService("TermService");
        return new HealthCheck
        {
            Name = "Terminal Services running",
            State = running ? HealthState.Ok : HealthState.Failed,
            Detail = detail,
            Consequence = "No RDP connections of any kind can be accepted while this service is stopped.",
            Fix = running ? FixAction.None : FixAction.StartTermService,
            FixLabel = "Start service"
        };
    }

    /// <summary>
    /// The important one. rdpwrap.ini is keyed by exact termsrv.dll version;
    /// a missing section means the patch offsets are unknown for this build.
    /// </summary>
    private HealthCheck CheckIniSupportsBuild()
    {
        var dir = ResolveInstallDirectory();
        var ini = dir is null ? null : Path.Combine(dir, "rdpwrap.ini");
        var version = TermSrvVersion;

        if (ini is null || !File.Exists(ini))
        {
            return new HealthCheck
            {
                Name = "rdpwrap.ini supports this Windows build",
                State = HealthState.Failed,
                Detail = "rdpwrap.ini not found",
                Consequence = "Without the offsets file the wrapper cannot patch anything.",
                Fix = FixAction.UpdateRdpWrapIni,
                FixLabel = "Update ini"
            };
        }

        var (supported, newestKnown) = IniSupports(ini, version);

        return new HealthCheck
        {
            Name = "rdpwrap.ini supports this Windows build",
            State = supported ? HealthState.Ok : HealthState.Failed,
            Detail = supported
                ? $"termsrv.dll {version} found in ini"
                : $"termsrv.dll is {version}; newest entry in ini is {newestKnown ?? "none"}",
            Consequence = "Windows updated termsrv.dll past what your ini knows about. Multi-session is silently broken: a second connection will take over your existing session instead of running beside it.",
            Fix = supported ? FixAction.None : FixAction.UpdateRdpWrapIni,
            FixLabel = "Run ini updater"
        };
    }

    private HealthCheck CheckRdpEnabled()
    {
        var deny = Registry.LocalMachine.OpenSubKey(TerminalServerKey)?.GetValue("fDenyTSConnections");
        var enabled = deny is int i && i == 0;

        return new HealthCheck
        {
            Name = "Remote Desktop connections allowed",
            State = enabled ? HealthState.Ok : HealthState.Failed,
            Detail = enabled ? "fDenyTSConnections = 0" : $"fDenyTSConnections = {deny?.ToString() ?? "unset"}",
            Consequence = "Windows will refuse every incoming connection, including loopback ones from this app.",
            Fix = enabled ? FixAction.None : FixAction.EnableRdpConnections,
            FixLabel = "Enable"
        };
    }

    private HealthCheck CheckMultipleSessionsPerUser()
    {
        var single = Registry.LocalMachine.OpenSubKey(TerminalServerKey)?.GetValue("fSingleSessionPerUser");
        var allowsMultiple = single is int i && i == 0;

        return new HealthCheck
        {
            Name = "Multiple sessions per user allowed",
            State = allowsMultiple ? HealthState.Ok : HealthState.Warning,
            Detail = allowsMultiple ? "fSingleSessionPerUser = 0" : $"fSingleSessionPerUser = {single?.ToString() ?? "unset"}",
            Consequence = "With this on, connecting as an account that is already signed in takes over the existing session instead of opening a second one. Harmless while every alt has its own account, fatal the moment two share one.",
            Fix = allowsMultiple ? FixAction.None : FixAction.AllowMultipleSessionsPerUser,
            FixLabel = "Allow multiple"
        };
    }

    /// <summary>
    /// Minimising an RDP window makes Windows stop rendering that session's
    /// desktop, which blinds any pixel- or image-search macro inside it.
    /// Value 2 keeps the remote desktop composed while minimised.
    /// </summary>
    private HealthCheck CheckSuppressWhenMinimized()
    {
        var value = Registry.CurrentUser.OpenSubKey(TsClientKey)?
            .GetValue("RemoteDesktop_SuppressWhenMinimized");
        var ok = value is int i && i == 2;

        return new HealthCheck
        {
            Name = "Minimised sessions keep rendering",
            State = ok ? HealthState.Ok : HealthState.Warning,
            Detail = ok ? "RemoteDesktop_SuppressWhenMinimized = 2" : $"value = {value?.ToString() ?? "unset"}",
            Consequence = "Minimise an alt's window and its desktop stops drawing. Pixel-based macros such as Natro go blind and stall until you restore the window.",
            Fix = ok ? FixAction.None : FixAction.SetSuppressWhenMinimized,
            FixLabel = "Fix registry"
        };
    }

    private HealthCheck CheckListener()
    {
        bool listening;
        try
        {
            listening = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(ep => ep.Port == 3389);
        }
        catch
        {
            listening = false;
        }

        return new HealthCheck
        {
            Name = "Listening on port 3389",
            State = listening ? HealthState.Ok : HealthState.Failed,
            Detail = listening ? "socket open" : "nothing bound to 3389",
            Consequence = "There is no RDP endpoint to connect to, so every launch will fail immediately.",
            Fix = listening ? FixAction.None : FixAction.StartTermService,
            FixLabel = "Start service"
        };
    }

    // ------------------------------------------------------------------- fixes

    public (bool ok, string message) ApplyFix(FixAction action)
    {
        try
        {
            switch (action)
            {
                case FixAction.EnableRdpConnections:
                    Registry.SetValue($@"HKEY_LOCAL_MACHINE\{TerminalServerKey}", "fDenyTSConnections", 0, RegistryValueKind.DWord);
                    return (true, "Remote Desktop connections enabled.");

                case FixAction.AllowMultipleSessionsPerUser:
                    Registry.SetValue($@"HKEY_LOCAL_MACHINE\{TerminalServerKey}", "fSingleSessionPerUser", 0, RegistryValueKind.DWord);
                    return (true, "Multiple sessions per user allowed.");

                case FixAction.SetSuppressWhenMinimized:
                    using (var key = Registry.CurrentUser.CreateSubKey(TsClientKey))
                    {
                        key.SetValue("RemoteDesktop_SuppressWhenMinimized", 2, RegistryValueKind.DWord);
                    }
                    return (true, "Minimised sessions will keep rendering. Close any open mstsc windows for this to take effect.");

                case FixAction.StartTermService:
                    return StartTermService();

                // InstallRdpWrap and UpdateRdpWrapIni download from the network,
                // so they run through the async path in the view model instead.
                default:
                    return (false, "No fix available.");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"fix {action} failed: {ex}");
            return (false, ex.Message);
        }
    }

    private static (bool, string) StartTermService()
    {
        try
        {
            using var sc = new ServiceController("TermService");
            if (sc.Status == ServiceControllerStatus.Running) return (true, "Already running.");
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            return (true, "Terminal Services started.");
        }
        catch (Exception ex)
        {
            return (false, $"Could not start TermService: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ self-heal

    // The stock RDP Wrapper binaries. RDPWInst.exe carries rdpwrap.dll as a
    // resource and drops it into Program Files on -i, so this one small zip is
    // all that is needed to install the wrapper itself. The offsets that decide
    // whether it actually works come from the ini below, not from here.
    private const string RdpWrapZipUrl =
        "https://github.com/stascorp/rdpwrap/releases/download/v1.6.2/RDPWrap-v1.6.2.zip";

    // The community-maintained ini. The original project's is frozen at 2017 and
    // knows no build past Windows 10 1803, which is exactly why multi-session
    // silently breaks on a current machine. This one is updated continuously and
    // carries sections for current Windows 11 builds.
    private const string MaintainedIniUrl =
        "https://raw.githubusercontent.com/sebaxakerhtc/rdpwrap.ini/master/rdpwrap.ini";

    /// <summary>
    /// Installs RDP Wrapper from nothing: downloads the binaries, runs the
    /// installer, then lays down an ini that supports this exact Windows build.
    /// Everything the "RDP Wrapper installed" and "Service hooked" checks need.
    /// </summary>
    public async Task<(bool ok, string message)> InstallRdpWrapperAsync(IProgress<string>? progress = null)
    {
        var work = Path.Combine(Path.GetTempPath(), "BssRdpWrapInstall");

        try
        {
            TryDeleteDirectory(work);
            Directory.CreateDirectory(work);

            var zipPath = Path.Combine(work, "rdpwrap.zip");

            progress?.Report("Downloading RDP Wrapper...");
            using (var http = NewHttp())
            using (var resp = await http.GetAsync(RdpWrapZipUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
            }

            progress?.Report("Extracting...");
            var extract = Path.Combine(work, "files");
            ZipFile.ExtractToDirectory(zipPath, extract);

            var installer = Directory
                .EnumerateFiles(extract, "RDPWInst.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (installer is null)
                return (false, "The RDP Wrapper download did not contain RDPWInst.exe.");

            progress?.Report("Installing RDP Wrapper...");
            // -i install, -o override the unsupported-OS refusal (the OS is only
            // "unsupported" because the bundled 2017 ini is; the fresh ini fixes
            // that a moment later). RDPWInst also sets the TermService ServiceDll,
            // opens the firewall and restarts the service.
            var (ran, _, _) = RunProcess(installer, "-i -o", Path.GetDirectoryName(installer)!);
            if (!ran)
                return (false, "The RDP Wrapper installer would not run. Your antivirus may have blocked it -- exclude the folder and try again.");

            // Confirm the dll actually landed. Antivirus quarantining rdpwrap.dll
            // is the usual failure here and it is silent, so check for it by name.
            var dir = ResolveInstallDirectory();
            var dll = dir is null ? null : Path.Combine(dir, "rdpwrap.dll");
            if (dll is null || !File.Exists(dll))
                return (false,
                    "RDP Wrapper did not install -- your antivirus most likely removed rdpwrap.dll. " +
                    "Add an exclusion for \"C:\\Program Files\\RDP Wrapper\", then try again.");

            progress?.Report("Fetching the configuration for your Windows build...");
            var ini = await UpdateIniAsync(progress).ConfigureAwait(false);

            return ini.ok
                ? (true, $"RDP Wrapper installed and configured. {ini.message}")
                : (false, $"RDP Wrapper is installed, but the configuration step failed: {ini.message}");
        }
        catch (Exception ex)
        {
            Log.Write($"rdp wrapper install failed: {ex}");
            return (false, $"Could not install RDP Wrapper: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    /// <summary>
    /// Replaces rdpwrap.ini with the maintained one and restarts Terminal
    /// Services so it takes effect. This is the fix for the most common and most
    /// confusing failure: the wrapper is installed and hooked, but its ini has no
    /// entry for the running termsrv.dll, so multi-session is silently dead.
    /// </summary>
    public async Task<(bool ok, string message)> UpdateIniAsync(IProgress<string>? progress = null)
    {
        try
        {
            var dir = ResolveInstallDirectory();
            if (dir is null)
                return (false, "RDP Wrapper is not installed yet -- install it first, then update the ini.");

            progress?.Report("Downloading the latest rdpwrap.ini...");
            string ini;
            using (var http = NewHttp())
            {
                ini = await http.GetStringAsync(MaintainedIniUrl).ConfigureAwait(false);
            }

            // A truncated download or an error page must never overwrite a
            // working ini. The real file is ~20k lines and opens with [Main].
            if (ini.Length < 4000 || !ini.Contains("[Main]", StringComparison.OrdinalIgnoreCase))
                return (false, "The downloaded rdpwrap.ini looked wrong, so it was left untouched. Try again in a moment.");

            var iniPath = Path.Combine(dir, "rdpwrap.ini");

            progress?.Report("Applying rdpwrap.ini...");
            try { if (File.Exists(iniPath)) File.Copy(iniPath, iniPath + ".bak", overwrite: true); }
            catch (Exception ex) { Log.Write($"could not back up rdpwrap.ini: {ex.Message}"); }

            var temp = iniPath + ".new";
            await File.WriteAllTextAsync(temp, ini).ConfigureAwait(false);
            File.Move(temp, iniPath, overwrite: true);

            progress?.Report("Restarting Terminal Services...");
            var (restarted, restartError) = RestartTermService();

            var version = TermSrvVersion;
            var (supported, newest) = IniSupports(iniPath, version);

            if (!supported)
                return (false,
                    $"The ini updated, but it still has no entry for termsrv.dll {version} " +
                    $"(newest listed is {newest ?? "none"}). Your Windows build may be brand new; check back once the ini catches up.");

            if (!restarted)
                return (true,
                    $"rdpwrap.ini now supports termsrv.dll {version}, but Terminal Services could not be restarted " +
                    $"({restartError}). Restart your PC to finish.");

            return (true, $"rdpwrap.ini updated -- multi-session now supports termsrv.dll {version}.");
        }
        catch (Exception ex)
        {
            Log.Write($"ini update failed: {ex}");
            return (false, $"Could not update rdpwrap.ini: {ex.Message}");
        }
    }

    private static HttpClient NewHttp()
    {
        // raw.githubusercontent.com is happy without one, but GitHub rejects a
        // request with no User-Agent, so set it on every client we make.
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BssAltManager");
        return http;
    }

    /// <summary>
    /// Stops TermService (and its dependents, which the service manager will not
    /// let us skip) and starts it again, so a freshly written rdpwrap.ini is
    /// reloaded without a reboot.
    /// </summary>
    private static (bool ok, string message) RestartTermService()
    {
        try
        {
            using var sc = new ServiceController("TermService");

            var dependents = sc.DependentServices
                .Where(d => d.Status != ServiceControllerStatus.Stopped)
                .ToList();

            foreach (var dep in dependents)
            {
                try
                {
                    dep.Stop();
                    dep.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }
                catch (Exception ex) { Log.Write($"could not stop dependent {dep.ServiceName}: {ex.Message}"); }
            }

            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }

            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));

            foreach (var dep in dependents)
            {
                try
                {
                    dep.Start();
                    dep.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                }
                catch (Exception ex) { Log.Write($"could not restart dependent {dep.ServiceName}: {ex.Message}"); }
            }

            return (true, "");
        }
        catch (Exception ex)
        {
            Log.Write($"restart TermService failed: {ex}");
            return (false, ex.Message);
        }
    }

    private static (bool ran, int exitCode, string output) RunProcess(
        string file, string args, string workingDir, int timeoutMs = 120_000)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p is null) return (false, -1, "process did not start");

            // Read before waiting so a chatty child cannot fill a pipe and block.
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (false, -1, "timed out");
            }

            var output = $"{stdout}\n{stderr}".Trim();
            Log.Write($"{Path.GetFileName(file)} {args} -> exit {p.ExitCode}: {output}");
            return (true, p.ExitCode, output);
        }
        catch (Exception ex)
        {
            Log.Write($"could not run {Path.GetFileName(file)} {args}: {ex.Message}");
            return (false, -1, ex.Message);
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { Log.Write($"could not clean up {dir}: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ helpers

    private static string? ResolveInstallDirectory()
    {
        var serviceDll = Registry.LocalMachine.OpenSubKey(TermServiceParamsKey)?
            .GetValue("ServiceDll") as string;

        if (!string.IsNullOrWhiteSpace(serviceDll) &&
            serviceDll.Contains("rdpwrap", StringComparison.OrdinalIgnoreCase))
        {
            var expanded = Environment.ExpandEnvironmentVariables(serviceDll);
            var dir = Path.GetDirectoryName(expanded);
            if (dir is not null && Directory.Exists(dir)) return dir;
        }

        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RDP Wrapper"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RDP Wrapper")
        })
        {
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Looks for a literal [major.minor.build.revision] section header matching
    /// the running termsrv.dll, and reports the newest entry present so the user
    /// can see how far behind the ini is.
    /// </summary>
    private static (bool supported, string? newestKnown) IniSupports(string iniPath, string version)
    {
        var wanted = $"[{version}]";
        var supported = false;
        Version? newest = null;

        foreach (var raw in File.ReadLines(iniPath))
        {
            var line = raw.Trim();
            if (line.Length < 3 || line[0] != '[' || line[^1] != ']') continue;

            if (string.Equals(line, wanted, StringComparison.OrdinalIgnoreCase)) supported = true;

            if (Version.TryParse(line[1..^1], out var parsed))
            {
                if (newest is null || parsed > newest) newest = parsed;
            }
        }
        return (supported, newest?.ToString());
    }

    private static (bool running, string detail) QueryService(string name)
    {
        try
        {
            using var sc = new ServiceController(name);
            return (sc.Status == ServiceControllerStatus.Running, sc.Status.ToString());
        }
        catch (Exception ex)
        {
            return (false, $"query failed: {ex.Message}");
        }
    }
}
