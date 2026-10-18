using System.Diagnostics;
using System.Text;

namespace ETSAssistant.WinUI3.Services;

public sealed record EtsDirectoryValidation(
    bool IsValid,
    string DirectoryPath,
    string? ExecutablePath,
    string Message,
    string? FileVersion = null);

public sealed partial class AssistantService
{
    public string EtsPathFile => Path.Combine(DataRoot, "ets_install_dir.txt");

    /// <summary>Checks that a folder is an ETS installation root and returns the exe to launch.</summary>
    public EtsDirectoryValidation ValidateEtsDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return new EtsDirectoryValidation(false, string.Empty, null, "请选择一个 ETS 安装文件夹。");

        string fullPath;
        try { fullPath = Path.GetFullPath(directory.Trim().Trim('"')); }
        catch (Exception ex) { return new EtsDirectoryValidation(false, directory, null, $"路径无效：{ex.Message}"); }

        if (!Directory.Exists(fullPath))
            return new EtsDirectoryValidation(false, fullPath, null, "选择的文件夹不存在，或当前账户无权访问。");

        string executable = Path.Combine(fullPath, "Ets.exe");
        string shell = Path.Combine(fullPath, "ETSShell.exe");
        if (!File.Exists(executable))
        {
            return File.Exists(shell)
                ? new EtsDirectoryValidation(false, fullPath, null,
                    "这个文件夹中只有 ETSShell.exe。请选择包含 Ets.exe 的 ETS 安装根目录。")
                : new EtsDirectoryValidation(false, fullPath, null,
                    "文件夹中未找到 Ets.exe。请选择 ETS 的安装根目录，例如 C:\\Program Files (x86)\\ETS。");
        }

        if (!File.Exists(shell) &&
            !File.Exists(Path.Combine(fullPath, "libcef.dll")) &&
            !File.Exists(Path.Combine(fullPath, "resources.pak")))
        {
            return new EtsDirectoryValidation(false, fullPath, executable,
                "找到的 Ets.exe 不像是完整的 ETS 安装（缺少 ETSShell.exe、libcef.dll 等文件）。");
        }

        try
        {
            var info = new FileInfo(executable);
            if (info.Length < 64 * 1024)
                return new EtsDirectoryValidation(false, fullPath, executable, "Ets.exe 文件大小异常，可能不是有效的程序。");
            using FileStream stream = File.OpenRead(executable);
            if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                return new EtsDirectoryValidation(false, fullPath, executable, "Ets.exe 不是有效的 Windows 可执行文件。");
        }
        catch (Exception ex)
        {
            return new EtsDirectoryValidation(false, fullPath, executable, $"无法读取 Ets.exe：{ex.Message}");
        }

        string? version = null;
        try
        {
            FileVersionInfo details = FileVersionInfo.GetVersionInfo(executable);
            version = string.IsNullOrWhiteSpace(details.FileVersion) ? null : details.FileVersion;
        }
        catch { }

        return new EtsDirectoryValidation(true, fullPath, executable, "目录校验通过。", version);
    }

    /// <summary>Persists a validated ETS directory and refreshes dependent shortcuts and listener.</summary>
    /// <returns>Non-fatal warnings, or an empty string when everything was refreshed.</returns>
    public string ApplyEtsDirectory(string directory)
    {
        EtsDirectoryValidation validation = ValidateEtsDirectory(directory);
        if (!validation.IsValid || validation.ExecutablePath is null)
            throw new InvalidOperationException(validation.Message);

        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(EtsPathFile, validation.DirectoryPath, new UTF8Encoding(false));
        EtsExe = validation.ExecutablePath;
        LastError = string.Empty;

        var warnings = new List<string>();
        if (IsAutostartEnabled)
        {
            string autostart = TryInstallAutostart(true);
            if (autostart.Length > 0) warnings.Add(autostart);
        }

        try { UpdateEtsShortcut(validation.ExecutablePath); }
        catch (Exception ex) { warnings.Add($"ETS 快捷方式更新失败：{ex.Message}"); }

        try { RestartListenerIfRunning(); }
        catch (Exception ex) { warnings.Add($"后台监听器重启失败：{ex.Message}"); }

        LastError = string.Join(" ", warnings);
        return LastError;
    }

    private string? FindSavedEtsExecutable()
    {
        try
        {
            if (!File.Exists(EtsPathFile)) return null;
            string directory = File.ReadAllText(EtsPathFile, Encoding.UTF8).Trim().Trim('"');
            if (directory.Length == 0) return null;
            string executable = Path.Combine(Path.GetFullPath(directory), "Ets.exe");
            return File.Exists(executable) ? executable : null;
        }
        catch { return null; }
    }

    private void UpdateEtsShortcut(string executable)
    {
        string shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ETS.lnk");
        if (File.Exists(shortcut))
            CreateDesktopShortcut(executable, "ETS.lnk", $"--remote-debugging-port={DebugPort}", force: true);
    }

    private void RestartListenerIfRunning()
    {
        Process[] listeners = Process.GetProcessesByName("ETS-Assistant-Engine");
        if (listeners.Length == 0) return;
        foreach (Process process in listeners)
        {
            try
            {
                process.Kill();
                process.WaitForExit(3000);
            }
            finally { process.Dispose(); }
        }
        EnsureListener();
    }
}
