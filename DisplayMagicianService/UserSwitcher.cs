using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DisplayMagicianService;

/// <summary>
/// Switches Windows user sessions from a SYSTEM service.
/// Uses tscon.exe for existing sessions and tsdiscon for fresh logins.
/// </summary>
public static class UserSwitcher
{
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer, uint reserved, uint version,
        out IntPtr ppSessionInfo, out uint pCount);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, uint sessionId, int wtsInfoClass,
        out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    private const int WTSUserName = 5;
    private const uint NO_SESSION = 0xFFFFFFFF;
    // WTS_CONNECTSTATE_CLASS: 0=Active, 1=Connected, 4=Disconnected

    private static bool IsValidUsername(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_.\-]+$");

    /// <summary>
    /// Find an existing session for the given username.
    /// </summary>
    public static uint FindUserSession(string username)
    {
        if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr sessionInfoPtr, out uint count))
        {
            try
            {
                var size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (uint i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(sessionInfoPtr + (int)(i * size));
                    if (info.SessionId == 0) continue;

                    if (WTSQuerySessionInformation(IntPtr.Zero, info.SessionId, WTSUserName,
                            out IntPtr buffer, out uint bytesReturned))
                    {
                        try
                        {
                            var sessionUser = Marshal.PtrToStringUni(buffer) ?? "";
                            if (sessionUser.Equals(username, StringComparison.OrdinalIgnoreCase))
                                return info.SessionId;
                        }
                        finally
                        {
                            WTSFreeMemory(buffer);
                        }
                    }
                }
            }
            finally
            {
                WTSFreeMemory(sessionInfoPtr);
            }
        }
        return NO_SESSION;
    }

    /// <summary>
    /// Switch to a user session.
    /// hasPassword=false: use tscon (automated, no auth needed)
    /// hasPassword=true: use tsdiscon (shows login screen, user must enter password)
    /// </summary>
    public static (bool success, string message) SwitchToUser(string username, bool hasPassword = false)
    {
        if (!IsValidUsername(username))
            return (false, $"Invalid username: '{username}'");

        try
        {
            uint currentSession = WTSGetActiveConsoleSessionId();
            uint existingSession = FindUserSession(username);

            if (hasPassword)
            {
                // Password-protected account: go to login screen, user logs in manually
                var result = RunCommand("tsdiscon.exe", $"{currentSession}");
                if (result.success)
                    return (true, $"Login screen shown. Enter password for '{username}'.");
                return (false, $"tsdiscon failed: {result.message}");
            }

            // No-password account: automate the switch
            if (existingSession != NO_SESSION)
            {
                // Existing session — connect directly
                var result = RunCommand("tscon.exe", $"{existingSession} /dest:console");
                if (result.success)
                    return (true, $"Switched to existing session for '{username}' (session {existingSession})");
                return (false, $"tscon failed: {result.message}");
            }

            // No existing session — go to login screen
            var disconnectResult = RunCommand("tsdiscon.exe", $"{currentSession}");
            if (disconnectResult.success)
                return (true, $"Login screen shown. Select '{username}' to log in.");
            return (false, $"tsdiscon failed: {disconnectResult.message}");
        }
        catch (Exception ex)
        {
            return (false, $"Exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Log off a specific session by ID.
    /// </summary>
    public static (bool success, string message) LogoffSession(uint sessionId)
    {
        return RunCommand("logoff.exe", $"{sessionId}");
    }

    private static (bool success, string message) RunCommand(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Failed to start process");

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);

            var output = $"{stdout} {stderr}".Trim();
            return (proc.ExitCode == 0, proc.ExitCode == 0 ? output : $"Exit code {proc.ExitCode}: {output}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
