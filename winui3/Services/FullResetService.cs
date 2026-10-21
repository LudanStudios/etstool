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
        var steps = new List<string>();
        var notes = new List<string>();

        int stopped = StopAssistantProcesses();
        steps.Add(stopped > 0 ? $"已停止 {stopped} 个后台进程（监听器/转换器/答题器）" : "后台监听器未在运行");

        steps.Add(RemoveAutostart());
        steps.Add(RemoveShortcuts());
        steps.Add(RemoveCredentials());
        steps.Add(ResetSettings());

        int removedMicFiles = RemoveVirtualMicrophoneFiles();
        steps.Add(removedMicFiles > 0 ? $"已删除 {removedMicFiles} 个虚拟麦克风/TTS 临时文件" : "没有虚拟麦克风临时文件");

        int removedLogs = RemoveLogs();
        steps.Add(removedLogs > 0 ? $"已删除 {removedLogs} 个助手日志" : "没有助手日志需要删除");

        try
        {
            var devices = new List<string>();
            foreach (DeviceInformation device in await DeviceInformation.FindAllAsync(DeviceClass.AudioRender))
                if (IsVirtualAudio(device.Name)) devices.Add(device.Name);
            foreach (DeviceInformation device in await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture))
                if (IsVirtualAudio(device.Name)) devices.Add(device.Name);

            notes.Add(devices.Count == 0
                ? "没有检测到 CABLE / VoiceMeeter 等第三方虚拟声卡。"
                : "检测到虚拟声卡：" + string.Join("、", devices.Distinct()) +
                  "。助手不会自动卸载音频驱动，需要时请在设备管理器或“应用和功能”里移除。");
        }
        catch (Exception)
        {
            notes.Add("无法枚举音频设备；如有虚拟声卡，请手动在设备管理器中检查。");
        }

        if (_assistant.IsEtsRunning)
            notes.Add("ETS 仍在运行；关闭 ETS 后，假麦克风参数才会真正失效。");
        notes.Add("试卷包、答题历史和答案记录已保留。");
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
                { }
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
            if (key?.GetValue(RunValueName) is null) return "登录启动项本来就不存在";
            key.DeleteValue(RunValueName, false);
            return "已移除登录启动项";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "无法移除登录启动项（权限不足）";
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        string startupLink = Path.Combine(startup, "ETS Assistant.lnk");
        try
        {
            if (File.Exists(startupLink)) { File.Delete(startupLink); removed++; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return removed > 0 ? $"已删除 {removed} 个快捷方式" : "没有需要删除的快捷方式";
    }

    private string RemoveCredentials()
    {
        ApiKeyStore.Clear();
        try { AliyunTtsCredentialStore.Clear(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return "已清除 DeepSeek API Key 和阿里云 TTS 凭证";
    }

    private string ResetSettings()
    {
        try
        {
            _assistant.SaveSettings(new AssistantSettings(1.0, "default", 0, false));
            DeleteFile(Path.Combine(_dataRoot, "ui_preferences.json"));
            DeleteFile(Path.Combine(_dataRoot, "ets_install_dir.txt"));
            return "答题设置已恢复默认，界面偏好已清除";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "答题设置未能完全重置：" + ex.Message;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
            return false;
        }
    }

    private static bool IsVirtualAudio(string name) =>
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual Audio", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);
}
