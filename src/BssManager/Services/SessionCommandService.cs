using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BssManager.Services;

/// <summary>
/// Runs something inside an alt's session.
///
/// This app runs in your session, and nothing it starts can appear on someone
/// else's desktop. The usual answer is a Windows service running as SYSTEM,
/// which can hand a process the other session's token -- a lot of machinery to
/// install on someone else's PC.
///
/// Task Scheduler already does it. A task registered against the alt with
/// TASK_LOGON_INTERACTIVE_TOKEN runs inside whatever session that account is
/// signed in to, started on demand by an administrator, with no password and no
/// service to install.
///
/// The task itself is fixed: it reads what to open from a file and opens it.
/// Only the file changes per launch, which keeps a short-lived ticket out of
/// the task definition and lets the script delete it the moment it is used.
///
/// The file is three lines -- target, arguments, working directory -- because
/// ShellExecute takes all three, and that one format covers both jobs: a
/// roblox-player: URL with the last two blank, and the macro's interpreter
/// with a script and a folder to run it from.
/// </summary>
public class SessionCommandService
{
    private const string TaskFolder = "BSS Alt Manager";

    private const int TaskActionExec = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelLua = 0;
    private const int TaskInstancesStopExisting = 3;

    private static string LaunchFolder => Path.Combine(AltSetupService.SharedFolder, "launch");
    private static string ScriptPath => Path.Combine(AltSetupService.SharedFolder, "open-in-session.vbs");

    private static string DropFile(string username) =>
        Path.Combine(LaunchFolder, $"{username}.url");

    private static string TaskName(string username) => $"Open in {username}";

    // ------------------------------------------------------------------ setup

    /// <summary>Writes the in-session script. Safe to call repeatedly.</summary>
    public void EnsureScript()
    {
        Directory.CreateDirectory(AltSetupService.SharedFolder);
        Directory.CreateDirectory(LaunchFolder);

        // Wscript rather than a .cmd: no console window flashes up on the alt's
        // desktop, and ShellExecute is the only thing that resolves a custom
        // protocol like roblox-player:.
        const string script = """
            ' Opens whatever BSS Alt Manager handed to this session.
            ' Written by the app -- edits are overwritten.
            Option Explicit

            Dim fso, shell, dropFile, handle, target, args, workDir

            Set fso = CreateObject("Scripting.FileSystemObject")
            Set shell = CreateObject("WScript.Shell")

            dropFile = shell.ExpandEnvironmentStrings("%ProgramData%") & _
                       "\BssAltManager\launch\" & _
                       shell.ExpandEnvironmentStrings("%USERNAME%") & ".url"

            If Not fso.FileExists(dropFile) Then
                WScript.Quit 0
            End If

            target = ""
            args = ""
            workDir = ""

            Set handle = fso.OpenTextFile(dropFile, 1)
            If Not handle.AtEndOfStream Then target = Trim(handle.ReadLine)
            If Not handle.AtEndOfStream Then args = Trim(handle.ReadLine)
            If Not handle.AtEndOfStream Then workDir = Trim(handle.ReadLine)
            handle.Close

            ' Delete before launching, not after: a launch URL carries a
            ' single-use ticket, and a crash between the two should not leave it
            ' readable on a shared drive.
            On Error Resume Next
            fso.DeleteFile dropFile, True
            On Error GoTo 0

            If Len(target) > 0 Then
                CreateObject("Shell.Application").ShellExecute target, args, workDir, "open", 1
            End If
            """;

        var existing = File.Exists(ScriptPath) ? File.ReadAllText(ScriptPath) : null;
        if (existing != script) File.WriteAllText(ScriptPath, script);
    }

    /// <summary>
    /// Registers the on-demand task for one alt. Called when an alt is created
    /// or repaired, not on every launch.
    /// </summary>
    public void EnsureTask(string username)
    {
        EnsureScript();

        var folder = OpenTaskFolder(create: true);
        if (folder is null) return;

        dynamic scheduler = folder.Scheduler;
        dynamic target = folder.Folder;

        dynamic definition = scheduler.NewTask(0);

        definition.RegistrationInfo.Author = "BSS Alt Manager";
        definition.RegistrationInfo.Description =
            $"Opens a link inside {username}'s session. Started on demand by BSS Alt Manager.";

        // InteractiveToken is what makes this land on the alt's desktop rather
        // than in a hidden session 0, and it needs no stored password.
        definition.Principal.UserId = $@"{Environment.MachineName}\{username}";
        definition.Principal.LogonType = TaskLogonInteractiveToken;
        definition.Principal.RunLevel = TaskRunLevelLua;

        definition.Settings.Enabled = true;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.RunOnlyIfIdle = false;
        definition.Settings.ExecutionTimeLimit = "PT2M";
        definition.Settings.MultipleInstances = TaskInstancesStopExisting;

        dynamic action = definition.Actions.Create(TaskActionExec);
        action.Path = "wscript.exe";
        action.Arguments = $"//B \"{ScriptPath}\"";

        target.RegisterTaskDefinition(
            TaskName(username), definition, TaskCreateOrUpdate,
            null, null, TaskLogonInteractiveToken, null);

        Log.Write($"registered in-session task for {username}");
    }

    public void RemoveTask(string username)
    {
        try
        {
            var folder = OpenTaskFolder(create: false);
            folder?.Folder.DeleteTask(TaskName(username), 0);
        }
        catch (Exception ex)
        {
            Log.Write($"could not remove the in-session task for {username}: {ex.Message}");
        }

        try
        {
            var drop = DropFile(username);
            if (File.Exists(drop)) File.Delete(drop);
        }
        catch { /* nothing important is in it */ }
    }

    // ---------------------------------------------------------------- sending

    /// <summary>
    /// Drops a URL into the alt's session and tells the task to open it.
    /// Returns once the session has consumed it, or once waiting stops being
    /// worthwhile.
    /// </summary>
    public Task<(bool ok, string message)> OpenInSessionAsync(
        string username, string url, CancellationToken token = default) =>
        SendAsync(username, url, "", "", token);

    /// <summary>
    /// Runs a program inside the alt's session. Same delivery as a URL -- the
    /// session picks it up, launches it and returns; nothing here waits for the
    /// program itself to finish or reports on how it went.
    /// </summary>
    public Task<(bool ok, string message)> RunInSessionAsync(
        string username, string executable, string arguments, string workingDirectory,
        CancellationToken token = default) =>
        SendAsync(username, executable, arguments, workingDirectory, token);

    private async Task<(bool ok, string message)> SendAsync(
        string username, string target, string arguments, string workingDirectory,
        CancellationToken token)
    {
        try
        {
            EnsureTask(username);
            WriteDropFile(username, target, arguments, workingDirectory);
        }
        catch (Exception ex)
        {
            Log.Write($"could not prepare the in-session launch for {username}: {ex}");
            return (false, $"Could not prepare the launch: {ex.Message}");
        }

        try
        {
            var folder = OpenTaskFolder(create: false);
            if (folder is null) return (false, "Task Scheduler is not available on this machine.");

            dynamic task = folder.Folder.GetTask(TaskName(username));
            task.Run(null);
        }
        catch (Exception ex)
        {
            Log.Write($"in-session task failed to start for {username}: {ex}");
            CleanUpDropFile(username);
            return (false, $"Could not start the task in {username}'s session: {ex.Message}");
        }

        // The script deletes the file as it reads it, so the file going away is
        // proof the session actually ran it -- better evidence than the task's
        // own result code, which reports on wscript, not on what it did.
        var consumed = await WaitForPickupAsync(username, TimeSpan.FromSeconds(25), token);

        if (consumed) return (true, "");

        CleanUpDropFile(username);
        return (false,
            $"{username}'s session did not pick the launch up. It is most likely not signed in yet.");
    }

    private static async Task<bool> WaitForPickupAsync(
        string username, TimeSpan timeout, CancellationToken token)
    {
        var drop = DropFile(username);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(drop)) return true;

            try { await Task.Delay(500, token); }
            catch (OperationCanceledException) { return false; }
        }

        return !File.Exists(drop);
    }

    private static void CleanUpDropFile(string username)
    {
        try
        {
            var drop = DropFile(username);
            if (File.Exists(drop)) File.Delete(drop);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Writes the launch where only the alt and administrators can read it.
    /// ProgramData is readable by every account on the machine by default, and
    /// a launch URL is a live credential for the seconds it survives.
    /// </summary>
    private static void WriteDropFile(
        string username, string target, string arguments, string workingDirectory)
    {
        Directory.CreateDirectory(LaunchFolder);

        var path = DropFile(username);

        // Fixed three lines, blank where unused: the script reads by position,
        // so a missing line would shift the arguments into the working
        // directory rather than fail.
        File.WriteAllText(path, $"{target}\r\n{arguments}\r\n{workingDirectory}\r\n");

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var account in new IdentityReference[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     new NTAccount(Environment.MachineName, username)
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                account, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(security);
    }

    // ------------------------------------------------------------------- COM

    /// <summary>Both handles are needed: the definition comes from the service, the registration from the folder.</summary>
    private sealed record TaskHandles(dynamic Scheduler, dynamic Folder);

    /// <summary>Connects to Task Scheduler and returns our folder.</summary>
    private static TaskHandles? OpenTaskFolder(bool create)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return null;

        dynamic? scheduler = Activator.CreateInstance(type);
        if (scheduler is null) return null;

        scheduler.Connect();
        dynamic root = scheduler.GetFolder("\\");

        try
        {
            return new TaskHandles(scheduler, root.GetFolder(TaskFolder));
        }
        catch
        {
            if (!create) return null;
            return new TaskHandles(scheduler, root.CreateFolder(TaskFolder, null));
        }
    }
}
