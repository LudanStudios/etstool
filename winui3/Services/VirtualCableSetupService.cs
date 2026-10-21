using System.ComponentModel;
using System.Diagnostics;

namespace ETSAssistant.WinUI3.Services;

/// <summary>Runs the bundled VB-CABLE installer and opens Windows recording settings.</summary>
public static class VirtualCableSetupService
{
    public static string InstallerPath(string appRoot) =>
        Path.Combine(appRoot, "tools", "VBCABLE", "VBCABLE_Setup_x64.exe");

    public static bool HasInstaller(string appRoot) => File.Exists(InstallerPath(appRoot));

    /// <summary>Starts the installer with UAC and waits for it to exit.</summary>
    public static async Task<int> RunInstallerAsync(string appRoot)
    {
        string path = InstallerPath(appRoot);
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 VB-CABLE 安装包。", path);

        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        using Process? process = Process.Start(startInfo);
        if (process is null) throw new InvalidOperationException("无法启动 VB-CABLE 安装程序。");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    public static void OpenRecordingDevices()
    {
        Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl,,1")
        {
            UseShellExecute = true
        });
    }

    public static void OpenSoundSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:sound")
        {
            UseShellExecute = true
        });
    }
}
