using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace ETSAssistant.WinUI3.Services;

public sealed record AssistantSettings(
    double Accuracy,
    string Seed,
    double SkipInterval,
    bool StartAtLogin,
    bool AutoListeningEnabled = false,
    bool ExperienceModeEnabled = false,
    bool VerboseLoggingEnabled = false);

public sealed partial class AssistantService
{
    private const int DebugPort = 10086;
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ETS Answer Listener";
    private static readonly Lazy<AssistantService> LazyCurrent = new(() => new AssistantService());
    public static AssistantService Current => LazyCurrent.Value;

    public string AppRoot { get; }
    public string DataRoot { get; }
    public string ConfigPath { get; }
    public string EnginePath { get; }
    public string ReadmePath { get; }
    public string? EtsExe { get; private set; }
    public string LastError { get; set; } = string.Empty;
    public bool IsEtsShellProcessRunning =>
        Process.GetProcessesByName("ETSShell").Length > 0 ||
        IsNamedProcessAtPath("Shell", EtsExe is null ? null : Path.Combine(Path.GetDirectoryName(EtsExe)!, "shell.exe"));
    public bool IsEtsProcessRunning => new[] { "Ets", "ETSShell", "EtsCore" }
        .Any(name => Process.GetProcessesByName(name).Length > 0) || IsEtsShellProcessRunning;
    public bool IsEtsRunning => IsEtsProcessRunning || IsDebugPortOpen();
    public bool IsAutostartEnabled => !string.IsNullOrWhiteSpace(ReadAutostartValue());

    private AssistantService()
    {
        AppRoot = FindAppRoot();
        DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ETS");
        ConfigPath = Path.Combine(AppRoot, "ets_answer_config.yaml");
        EnginePath = Path.Combine(AppRoot, "ETS-Assistant-Engine.exe");
        ReadmePath = Path.Combine(AppRoot, "README.md");
        RefreshEts();
    }

    private static string FindAppRoot()
    {
        string current = Path.GetFullPath(AppContext.BaseDirectory);
        while (true)
        {
            if (File.Exists(Path.Combine(current, "ets_answer_config.yaml")) ||
                File.Exists(Path.Combine(current, "ETS-Assistant-Engine.exe"))) return current;
            DirectoryInfo? parent = Directory.GetParent(current.TrimEnd(Path.DirectorySeparatorChar));
            if (parent is null) break;
            current = parent.FullName;
        }
        return Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName
               ?? AppContext.BaseDirectory;
    }

    public void RefreshEts() => EtsExe = FindSavedEtsExecutable() ?? FindEtsExecutable();

    public AssistantSettings LoadSettings()
    {
        double accuracy = 1.0, skip = 0.0;
        string seed = "default";
        bool autoListening = false, experienceMode = false;
        if (File.Exists(ConfigPath))
        {
            foreach (string rawLine in File.ReadLines(ConfigPath, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line[..colon].Trim();
                string value = line[(colon + 1)..].Split('#', 2)[0].Trim().Trim('"', '\'');
                if (key == "accuracy_rate" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)) accuracy = a;
                if (key == "random_seed") seed = value;
                if (key == "skip_interval_seconds" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double s)) skip = s;
                if (key == "auto_listening_enabled") autoListening = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (key == "experience_mode_enabled") experienceMode = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        AppPreferencesService preferences = AppPreferencesService.Current;
        return new AssistantSettings(Math.Clamp(accuracy, 0, 1), string.IsNullOrWhiteSpace(seed) ? "default" : seed,
            Math.Clamp(skip, 0, 60), IsAutostartEnabled,
            preferences.AutoListeningEnabled,
            preferences.ExperienceModeEnabled,
            preferences.VerboseLoggingEnabled);
    }

    public void SaveSettings(AssistantSettings settings)
    {
        if (!double.IsFinite(settings.Accuracy) || settings.Accuracy is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(settings), "目标正确率必须在 0% 到 100% 之间。");
        if (!double.IsFinite(settings.SkipInterval) || settings.SkipInterval is < 0 or > 60)
            throw new ArgumentOutOfRangeException(nameof(settings), "跳题间隔必须在 0 到 60 秒之间。");
        string seed = settings.Seed;
        if (string.IsNullOrWhiteSpace(seed) || seed == "default")
            seed = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        if (seed.Any(c => c is '\r' or '\n' or '#'))
            throw new ArgumentException("随机种子不能包含换行符或 # 字符。");
        Directory.CreateDirectory(AppRoot);
        string contents = "# 由 ETS 自动监听助手管理；也可在程序界面中修改。\n" +
            $"accuracy_rate: {settings.Accuracy.ToString("0.00", CultureInfo.InvariantCulture)}\n" +
            $"random_seed: '{seed}'\n" +
            $"skip_interval_seconds: {settings.SkipInterval.ToString("0.###", CultureInfo.InvariantCulture)}\n" +
            $"auto_listening_enabled: {settings.AutoListeningEnabled.ToString().ToLowerInvariant()}\n" +
            $"experience_mode_enabled: {settings.ExperienceModeEnabled.ToString().ToLowerInvariant()}\n" +
            $"verbose_logging_enabled: {settings.VerboseLoggingEnabled.ToString().ToLowerInvariant()}\n";
        File.WriteAllText(ConfigPath, contents, new UTF8Encoding(false));
    }

    public void SyncExperienceSettings()
    {
        try
        {
            AssistantSettings settings = LoadSettings();
            SaveSettings(settings with
            {
                AutoListeningEnabled = AppPreferencesService.Current.AutoListeningEnabled,
                ExperienceModeEnabled = AppPreferencesService.Current.ExperienceModeEnabled,
                VerboseLoggingEnabled = AppPreferencesService.Current.VerboseLoggingEnabled
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    /// <summary>Updates the login entry; returns a friendly warning instead of throwing when Windows blocks the registry write.</summary>
    public string TryInstallAutostart(bool enabled)
    {
        try
        {
            using RegistryKey? existing = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            string? current = existing?.GetValue(RunValueName) as string;

            if (!enabled)
            {
                if (string.IsNullOrWhiteSpace(current)) return string.Empty;
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                    ?? throw new InvalidOperationException("无法打开 Windows 登录启动项。");
                key.DeleteValue(RunValueName, false);
                return string.Empty;
            }

            string ets = EtsExe ?? FindEtsExecutable()
                ?? throw new FileNotFoundException("找不到 ETS 安装目录。请先重新扫描 ETS。", "Ets.exe");
            string command = $"{Quote(EnginePath)} --background-listener --root {Quote(DataRoot)} --config {Quote(ConfigPath)} --ets-exe {Quote(ets)} --debug-port {DebugPort}";
            if (string.Equals(current, command, StringComparison.Ordinal)) return string.Empty;

            using RegistryKey writeKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
                ?? throw new InvalidOperationException("无法打开 Windows 登录启动项。");
            writeKey.SetValue(RunValueName, command, RegistryValueKind.String);
            return string.Empty;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return "Windows 拒绝写入登录启动项（注册表 Run 键），登录后可能不会自动启动监听器；本次操作的其他功能不受影响。";
        }
    }

    public void InstallAutostart(bool enabled)
    {
        string warning = TryInstallAutostart(enabled);
        if (warning.Length > 0) throw new UnauthorizedAccessException(warning);
    }

    public void EnsureListener()
    {
        if (!File.Exists(EnginePath))
            throw new FileNotFoundException("后台组件不存在，请重新生成完整程序文件夹。", EnginePath);
        if (EtsExe is null) RefreshEts();
        if (EtsExe is null) throw new FileNotFoundException("找不到 ETS 安装目录。请确认 ETS 已安装，再重新扫描。", "Ets.exe");
        if (IsListenerRunning()) return;

        Directory.CreateDirectory(DataRoot);
        string args = $"--background-listener --root {Quote(DataRoot)} --config {Quote(ConfigPath)} --ets-exe {Quote(EtsExe)} --debug-port {DebugPort}";
        Process.Start(new ProcessStartInfo(EnginePath, args)
        {
            WorkingDirectory = AppRoot, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static bool IsListenerRunning()
    {
        try
        {
            using Mutex mutex = Mutex.OpenExisting(@"Local\ETSAnswerListenerEngine-v1");
            return true;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static bool IsDebugPortOpen()
    {
        try
        {
            using var client = new TcpClient();
            Task connect = client.ConnectAsync("127.0.0.1", DebugPort);
            return connect.Wait(120) && client.Connected;
        }
        catch (Exception) { return false; }
    }

    public void OpenEts(string extraArguments = "")
    {
        if (EtsExe is null) RefreshEts();
        if (EtsExe is null) throw new FileNotFoundException("找不到 ETS 安装目录。请确认 ETS 已安装，再重新扫描。", "Ets.exe");
        if (IsEtsRunning) return;
        string arguments = $"--remote-debugging-port={DebugPort}";
        if (!string.IsNullOrWhiteSpace(extraArguments)) arguments += " " + extraArguments;
        Process.Start(new ProcessStartInfo(EtsExe, arguments)
        {
            WorkingDirectory = Path.GetDirectoryName(EtsExe)!, UseShellExecute = true
        });
    }

    public int EndEtsProcesses()
    {
        int ended = 0;
        foreach (string name in new[] { "Ets", "ETSShell", "EtsCore", "Shell" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (name == "Shell" && EtsExe is not null &&
                            !IsSameExecutable(process, Path.Combine(Path.GetDirectoryName(EtsExe)!, "shell.exe")))
                            continue;
                        if (process.HasExited) continue;
                        process.Kill();
                        process.WaitForExit(2000);
                        ended++;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or
                                              UnauthorizedAccessException) { }
                }
            }
        }
        return ended;
    }

    private static bool IsNamedProcessAtPath(string name, string? expectedPath)
    {
        if (string.IsNullOrWhiteSpace(expectedPath)) return false;
        foreach (Process process in Process.GetProcessesByName(name))
        {
            using (process)
                if (IsSameExecutable(process, expectedPath)) return true;
        }
        return false;
    }

    private static bool IsSameExecutable(Process process, string expectedPath)
    {
        try
        {
            string? path = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or
                                   UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string InitializeAndOpen(AssistantSettings settings, string extraArguments = "")
    {
        RefreshEts();
        if (EtsExe is null) throw new FileNotFoundException("找不到 ETS 安装目录。请确认 ETS 已安装，再重新扫描。", "Ets.exe");
        SaveSettings(settings);

        var warnings = new List<string>();
        string autostart = TryInstallAutostart(settings.StartAtLogin);
        if (autostart.Length > 0) warnings.Add(autostart);

        EnsureListener();
        OpenEts(extraArguments);
        LastError = string.Join(" ", warnings);
        return LastError;
    }

    public string ReadRecentLogs()
    {
        string[] names = { "ets_answer_listener.log", "ets_auto_answer.log", "ets_converter.log", "ets_listener_boot.log" };
        var output = new List<string>();
        foreach (string name in names)
        {
            string path = Path.Combine(DataRoot, name);
            if (!File.Exists(path)) continue;
            try
            {
                string[] lines = File.ReadLines(path, Encoding.UTF8).TakeLast(60).ToArray();
                if (lines.Length == 0) continue;
                output.Add($"── {name} ──");
                output.AddRange(lines);
            }
            catch (IOException) { }
        }
        return output.Count == 0 ? "尚无运行记录。初始化后，ETS 启动时会显示监听状态。" : string.Join(Environment.NewLine, output.TakeLast(180));
    }

    private string? ReadAutostartValue()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false); return key?.GetValue(RunValueName) as string; }
        catch { return null; }
    }

    private static void CreateDesktopShortcut(string target, string fileName, string arguments, bool force = false)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(desktop);
        string path = Path.Combine(desktop, fileName);
        if (!force && File.Exists(path)) return;

        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows 快捷方式组件不可用。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = target; shortcut.Arguments = arguments;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target)!;
        shortcut.IconLocation = $"{target},0"; shortcut.WindowStyle = 1; shortcut.Save();
    }

    private static bool ShortcutMatches(string path, string target, string arguments)
    {
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(path);
            string? currentTarget = shortcut.TargetPath as string;
            string? currentArguments = shortcut.Arguments as string;
            return !string.IsNullOrWhiteSpace(currentTarget) &&
                   string.Equals(Path.GetFullPath(currentTarget), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(currentArguments ?? string.Empty, arguments, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
