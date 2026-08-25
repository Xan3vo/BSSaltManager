using System.IO;
using System.Diagnostics;
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

                case FixAction.UpdateRdpWrapIni:
                    return RunIniUpdater();

                case FixAction.InstallRdpWrap:
                    return OpenRdpWrapDownload();

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

    /// <summary>
    /// Runs RDP Wrapper's own updater rather than reimplementing offset
    /// discovery. It needs internet access and restarts TermService, which drops
    /// live sessions, so the UI warns before calling this.
    /// </summary>
    private static (bool, string) RunIniUpdater()
    {
        var dir = ResolveInstallDirectory();
        if (dir is null) return (false, "RDP Wrapper install folder not found.");

        var updater = new[] { "autoupdate.bat", "update.bat" }
            .Select(f => Path.Combine(dir, f))
            .FirstOrDefault(File.Exists);

        if (updater is null)
            return (false, "No autoupdate.bat or update.bat in the RDP Wrapper folder.");

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{updater}\"\"",
            WorkingDirectory = dir,
            UseShellExecute = true,
            Verb = "runas"
        });
        return (true, "Updater launched in a console window. Re-run the health check when it finishes.");
    }

    private static (bool, string) OpenRdpWrapDownload()
    {
        var dir = ResolveInstallDirectory();
        var installer = dir is null ? null : Path.Combine(dir, "RDPWInst.exe");

        if (installer is not null && File.Exists(installer))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "-i -o",
                UseShellExecute = true,
                Verb = "runas"
            });
            return (true, "Reinstalling the wrapper via RDPWInst.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/stascorp/rdpwrap/releases",
            UseShellExecute = true
        });
        return (true, "Opened the RDP Wrapper releases page in your browser.");
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
