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
            return new EtsDirectoryValidation(false, string.Empty, null, LocalizationService.Current.Translate("ets_installation.choose_folder"));

        string fullPath;
        try { fullPath = Path.GetFullPath(directory.Trim().Trim('"')); }
        catch (Exception ex) { return new EtsDirectoryValidation(false, directory, null, LocalizationService.Current.Format("ets_installation.invalid_path", ("detail", ex.Message))); }

        if (!Directory.Exists(fullPath))
            return new EtsDirectoryValidation(false, fullPath, null, LocalizationService.Current.Translate("ets_installation.folder_not_accessible"));

        string executable = Path.Combine(fullPath, "Ets.exe");
        string shell = Path.Combine(fullPath, "ETSShell.exe");
        if (!File.Exists(executable))
        {
            return File.Exists(shell)
                ? new EtsDirectoryValidation(false, fullPath, null,
                    LocalizationService.Current.Translate("ets_installation.only_shell_found"))
                : new EtsDirectoryValidation(false, fullPath, null,
                    LocalizationService.Current.Translate("ets_installation.exe_not_found"));
        }

        if (!File.Exists(shell) &&
            !File.Exists(Path.Combine(fullPath, "libcef.dll")) &&
            !File.Exists(Path.Combine(fullPath, "resources.pak")))
        {
            return new EtsDirectoryValidation(false, fullPath, executable,
                LocalizationService.Current.Translate("ets_installation.incomplete_installation"));
        }

        try
        {
            var info = new FileInfo(executable);
            if (info.Length < 64 * 1024)
                return new EtsDirectoryValidation(false, fullPath, executable, LocalizationService.Current.Translate("ets_installation.exe_size_invalid"));
            using FileStream stream = File.OpenRead(executable);
            if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                return new EtsDirectoryValidation(false, fullPath, executable, LocalizationService.Current.Translate("ets_installation.exe_invalid"));
        }
        catch (Exception ex)
        {
            return new EtsDirectoryValidation(false, fullPath, executable, LocalizationService.Current.Format("ets_installation.exe_read_failed", ("detail", ex.Message)));
        }

        string? version = null;
        try
        {
            FileVersionInfo details = FileVersionInfo.GetVersionInfo(executable);
            version = string.IsNullOrWhiteSpace(details.FileVersion) ? null : details.FileVersion;
        }
        catch { }

        return new EtsDirectoryValidation(true, fullPath, executable, LocalizationService.Current.Translate("ets_installation.validation_passed"), version);
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
        catch (Exception ex) { warnings.Add(LocalizationService.Current.Format("ets_installation.shortcut_update_failed", ("detail", ex.Message))); }

        try { RestartListenerIfRunning(); }
        catch (Exception ex) { warnings.Add(LocalizationService.Current.Format("ets_installation.listener_restart_failed", ("detail", ex.Message))); }

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
