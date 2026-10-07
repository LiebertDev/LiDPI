using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace LiDPI.UI.Services;

/// <summary>
/// "Start with Windows" via Task Scheduler. The app requires elevation and a Run-key entry
/// would not elevate, so a logon task with highest privileges is used instead. The task is
/// registered from XML so the defaults that would kill or block a long-running app
/// (72h execution limit, battery conditions) are switched off.
/// </summary>
public static class AutostartService
{
    private const string TaskName = "LiDPI";

    /// <summary>True if the scheduled task exists.</summary>
    public static async Task<bool> IsEnabledAsync() =>
        await RunSchtasksAsync("/Query", "/TN", TaskName).ConfigureAwait(false) == 0;

    /// <summary>Create (or overwrite) or delete the logon task. Returns false on failure.</summary>
    public static async Task<bool> SetEnabledAsync(bool enable)
    {
        if (!enable)
        {
            if (!await IsEnabledAsync().ConfigureAwait(false)) return true;
            return await RunSchtasksAsync("/Delete", "/TN", TaskName, "/F").ConfigureAwait(false) == 0;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        string xmlPath = Path.Combine(Path.GetTempPath(), $"LiDPI-task-{Environment.ProcessId}-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(exe), Encoding.Unicode);   // UTF-16 LE + BOM
            return await RunSchtasksAsync("/Create", "/TN", TaskName, "/XML", xmlPath, "/F").ConfigureAwait(false) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* ignore */ }
        }
    }

    private static string BuildTaskXml(string exe)
    {
        string user;
        using (var id = WindowsIdentity.GetCurrent()) user = id.Name;
        string u = SecurityElement.Escape(user);
        string cmd = SecurityElement.Escape(exe);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>LiDPI - Windows ile başlat</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{u}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{u}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{cmd}</Command>
                  <Arguments>--tray</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static async Task<int> RunSchtasksAsync(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return -1;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(); } catch { } return -1; }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
