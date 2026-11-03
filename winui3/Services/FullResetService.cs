using System.Diagnostics;
using Microsoft.Win32;
using Windows.Devices.Enumeration;

namespace ETSAssistant.WinUI3.Services;

public sealed record FullResetResult(IReadOnlyList<string> Steps, IReadOnlyList<string> Notes);

/// <summary>
/// Removes the assistant's background listener, autostart entry, shortcuts,
/// credentials and virtual-microphone files. Exam packs and answer history are
/// intentionally preserved.
/// </summary>
public sealed class FullResetService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ETS Answer Listener";
    private static readonly string[] AssistantProcesses =
        { "ETS-Assistant-Engine", "ETS-Assistant-Fast", "ETS-Assistant" };
    private static readonly string[] DesktopShortcuts =
        { "ETS 自动监听助手.lnk", "ETS 自动监听助手（快速启动）.lnk" };
    private static readonly string[] LogFiles =
    {
        "ets_answer_listener.log", "ets_auto_answer.log", "ets_converter.log",
        "ets_winui_startup.log", "ets_listener_boot.log"
    };

    private readonly AssistantService _assistant;
    private readonly string _appRoot;
    private readonly string _dataRoot;

    public FullResetService(AssistantService assistant)
    {
        _assistant = assistant;
        _appRoot = assistant.AppRoot;
        _dataRoot = assistant.DataRoot;
    }

    public async Task<FullResetResult> RunAsync()
    {
        DiagnosticLogService.Info("developer_tools.full_reset.started");
        var steps = new List<string>();
        var notes = new List<string>();

        int stopped = StopAssistantProcesses();
        steps.Add(stopped > 0 ? F("reset.stopped_processes", ("count", stopped)) : L("reset.listener_not_running"));

        steps.Add(RemoveAutostart());
        steps.Add(RemoveShortcuts());
        steps.Add(RemoveCredentials());
        steps.Add(ResetSettings());

        int removedMicFiles = RemoveVirtualMicrophoneFiles();
        steps.Add(removedMicFiles > 0 ? F("reset.removed_audio_files", ("count", removedMicFiles)) : L("reset.no_audio_files"));

        int removedLogs = RemoveLogs();
        steps.Add(removedLogs > 0 ? F("reset.removed_logs", ("count", removedLogs)) : L("reset.no_logs"));
        int diagnosticLogCount = DiagnosticLogService.GetLogFiles().Count;
        DiagnosticLogService.ClearLogs();

        try
        {
            var devices = new List<string>();
            foreach (DeviceInformation device in await DeviceInformation.FindAllAsync(DeviceClass.AudioRender))
                if (IsVirtualAudio(device.Name)) devices.Add(device.Name);
            foreach (DeviceInformation device in await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture))
                if (IsVirtualAudio(device.Name)) devices.Add(device.Name);

            notes.Add(devices.Count == 0
                ? L("reset.no_third_party_virtual_audio")
                : F("reset.virtual_audio_devices_detected", ("devices", string.Join(", ", devices.Distinct()))));
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warn("developer_tools.full_reset.audio_device_scan.failed", ex.Message);
            notes.Add(L("reset.audio_device_enumeration_failed"));
        }

        if (_assistant.IsEtsRunning)
            notes.Add(L("reset.ets_still_running"));
        notes.Add(L("reset.papers_and_answer_history_kept"));
        DiagnosticLogService.Info("developer_tools.full_reset.completed",
            $"stoppedProcesses={stopped} removedLogs={removedLogs + diagnosticLogCount}");
        return new FullResetResult(steps, notes);
    }

    private static int StopAssistantProcesses()
    {
        int stopped = 0;
        foreach (string name in AssistantProcesses)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                try
                {
                    process.Kill(true);
                    process.WaitForExit(3000);
                    stopped++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    DiagnosticLogService.Warn("developer_tools.full_reset.process_stop.failed",
                        $"process={name} pid={SafeProcessId(process)} detail={ex.Message}");
                }
                finally { process.Dispose(); }
            }
        }
        return stopped;
    }

    private static string RemoveAutostart()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key?.GetValue(RunValueName) is null) return L("reset.autostart_not_present");
            key.DeleteValue(RunValueName, false);
            return L("reset.autostart_removed");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            DiagnosticLogService.Warn("developer_tools.full_reset.autostart_remove.failed", ex.Message);
            return L("reset.autostart_remove_denied");
        }
    }

    private static string RemoveShortcuts()
    {
        int removed = 0;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (string name in DesktopShortcuts)
        {
            string path = Path.Combine(desktop, name);
            try
            {
                if (File.Exists(path)) { File.Delete(path); removed++; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { DiagnosticLogService.Warn("developer_tools.full_reset.shortcut_remove.failed", ex.Message); }
        }

        string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        string startupLink = Path.Combine(startup, "ETS Assistant.lnk");
        try
        {
            if (File.Exists(startupLink)) { File.Delete(startupLink); removed++; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DiagnosticLogService.Warn("developer_tools.full_reset.shortcut_remove.failed", ex.Message); }

        return removed > 0 ? F("reset.removed_shortcuts", ("count", removed)) : L("reset.no_shortcuts");
    }

    private string RemoveCredentials()
    {
        ApiKeyStore.Clear();
        try { AliyunTtsCredentialStore.Clear(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DiagnosticLogService.Warn("developer_tools.full_reset.credential_remove.failed", ex.Message); }
        return L("reset.credentials_cleared");
    }

    private string ResetSettings()
    {
        try
        {
            _assistant.SaveSettings(new AssistantSettings(1.0, "default", 0, false));
            DeleteFile(Path.Combine(_dataRoot, "ui_preferences.json"));
            DeleteFile(Path.Combine(_dataRoot, "ets_install_dir.txt"));
            return L("reset.settings_restored");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogService.Warn("developer_tools.full_reset.settings_reset.failed", ex.Message);
            return F("reset.settings_restore_failed", ("detail", ex.Message));
        }
    }

    private int RemoveVirtualMicrophoneFiles()
    {
        int removed = 0;
        string assistantFolder = Path.Combine(_dataRoot, "_assistant");
        foreach (string name in new[]
                 {
                     "speaking_input.wav", "reset_request.json", "reset_ack.json", "speaking_session.json"
                 })
            if (DeleteFile(Path.Combine(assistantFolder, name))) removed++;

        string ttsFolder = Path.Combine(_appRoot, "temp", "tts-test");
        try
        {
            if (Directory.Exists(ttsFolder))
            {
                foreach (string file in Directory.EnumerateFiles(ttsFolder, "*.wav"))
                    if (DeleteFile(file)) removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DiagnosticLogService.Warn("developer_tools.full_reset.temporary_file_remove.failed", ex.Message); }
        if (DeleteFile(Path.Combine(_appRoot, "temp", "speaking_session.json"))) removed++;
        return removed;
    }

    private int RemoveLogs()
    {
        int removed = 0;
        foreach (string name in LogFiles)
            if (DeleteFile(Path.Combine(_dataRoot, name))) removed++;

        try
        {
            foreach (string file in Directory.EnumerateFiles(_dataRoot, "ets_answer_listener.log.old-*"))
                if (DeleteFile(file)) removed++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { DiagnosticLogService.Warn("developer_tools.full_reset.log_remove.failed", ex.Message); }
        return removed;
    }

    private static bool DeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogService.Warn("developer_tools.full_reset.file_remove.failed", ex.Message);
            return false;
        }
    }

    private static bool IsVirtualAudio(string name) =>
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual Audio", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    private static string L(string key) => LocalizationService.Current.Translate(key);
    private static string F(string key, params (string Name, object? Value)[] values) =>
        LocalizationService.Current.Format(key, values);

    private static int SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch (InvalidOperationException) { return 0; }
    }
}
