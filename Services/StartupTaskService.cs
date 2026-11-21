using System.Diagnostics;

namespace NetPrio.Services;

public static class StartupTaskService
{
    private const string TaskName = "NetPrioAutoStart";

    public static bool IsEnabled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    public static bool SetEnabled(bool enable)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return false;

        string args = enable
            ? $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" --minimized\" /SC ONLOGON /RL HIGHEST /F"
            : $"/Delete /TN \"{TaskName}\" /F";

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}
