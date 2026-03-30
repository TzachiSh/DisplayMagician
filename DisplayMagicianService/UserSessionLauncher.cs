using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DisplayMagicianService;

/// <summary>
/// Launches processes in the active user's session from a SYSTEM service.
/// Required because Session 0 (SYSTEM) cannot access display hardware.
/// </summary>
public static class UserSessionLauncher
{
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int impersonationLevel,
        int tokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    private const uint NO_SESSION = 0xFFFFFFFF;

    private static Dictionary<string, string> ParseEnvironmentBlock(IntPtr envBlock)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (true)
        {
            var entry = Marshal.PtrToStringUni(envBlock + offset);
            if (string.IsNullOrEmpty(entry)) break;
            var eq = entry.IndexOf('=', 1); // skip first char (can be =)
            if (eq > 0)
                dict[entry[..eq]] = entry[(eq + 1)..];
            offset += (entry.Length + 1) * 2; // unicode chars + null
        }
        return dict;
    }

    private static IntPtr CreateCustomEnvironmentBlock(Dictionary<string, string> envVars)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var kv in envVars.OrderBy(k => k.Key))
        {
            sb.Append(kv.Key);
            sb.Append('=');
            sb.Append(kv.Value);
            sb.Append('\0');
        }
        sb.Append('\0'); // double null terminator

        var bytes = System.Text.Encoding.Unicode.GetBytes(sb.ToString());
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint TOKEN_ALL_ACCESS = 0x000F01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint WAIT_TIMEOUT = 0x00000102;

    /// <summary>
    /// Run a process in the active user's session and wait for it to complete.
    /// Returns (exitCode, success)
    /// </summary>
    public static (uint exitCode, bool success, string error) RunInUserSession(
        string exePath, string arguments, int timeoutMs = 60000, Dictionary<string, string>? envVars = null, bool fireAndForget = false)
    {
        IntPtr userToken = IntPtr.Zero;
        IntPtr dupToken = IntPtr.Zero;
        IntPtr envBlock = IntPtr.Zero;
        bool customEnvBlock = false;

        try
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == NO_SESSION)
                return (1, false, "No active console session");

            if (!WTSQueryUserToken(sessionId, out userToken))
                return (1, false, $"WTSQueryUserToken failed: {Marshal.GetLastWin32Error()}");

            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out dupToken))
                return (1, false, $"DuplicateTokenEx failed: {Marshal.GetLastWin32Error()}");

            CreateEnvironmentBlock(out envBlock, dupToken, false);

            // Inject custom environment variables if provided
            if (envVars != null && envVars.Count > 0 && envBlock != IntPtr.Zero)
            {
                // Parse existing env block, add our vars, create new block
                var envDict = ParseEnvironmentBlock(envBlock);
                DestroyEnvironmentBlock(envBlock);
                envBlock = IntPtr.Zero;

                foreach (var kv in envVars)
                    envDict[kv.Key] = kv.Value;

                envBlock = CreateCustomEnvironmentBlock(envDict);
                customEnvBlock = true;
            }

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "winsta0\\default";

            var cmdLine = $"\"{exePath}\" {arguments}";
            uint flags = CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT;

            if (!CreateProcessAsUser(dupToken, null, cmdLine,
                    IntPtr.Zero, IntPtr.Zero, false, flags,
                    envBlock, null, ref si, out var pi))
                return (1, false, $"CreateProcessAsUser failed: {Marshal.GetLastWin32Error()}");

            try
            {
                if (fireAndForget)
                {
                    // Don't wait — process launched successfully
                    return (0, true, "Launched");
                }

                // Wait for process to finish
                var waitResult = WaitForSingleObject(pi.hProcess, (uint)timeoutMs);
                if (waitResult == WAIT_TIMEOUT)
                {
                    return (1, false, "Process timed out");
                }

                GetExitCodeProcess(pi.hProcess, out uint exitCode);
                return (exitCode, exitCode == 0, exitCode == 0 ? "Success" : $"Exit code: {exitCode}");
            }
            finally
            {
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
        }
        catch (Exception ex)
        {
            return (1, false, $"Exception: {ex.Message}");
        }
        finally
        {
            if (envBlock != IntPtr.Zero)
            {
                if (customEnvBlock) Marshal.FreeHGlobal(envBlock);
                else DestroyEnvironmentBlock(envBlock);
            }
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }
}
