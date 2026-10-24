using System.Diagnostics;

namespace ETSAssistant.WinUI3.Services;

/// <summary>Runs the speaking automation through the Python runtime bundled with the engine.</summary>
public sealed class SpeakingAutoRunner
{
    private readonly object _sync = new();
    private Process? _process;
    private System.Threading.Timer? _logTimer;
    private string? _logPath;
    private int _readLineCount;

    public bool IsRunning
    {
        get
        {
            lock (_sync) return _process is { HasExited: false };
        }
    }

    public event Action<string>? Output;
    public event Action? Exited;

    public bool Start(string appRoot, string dataRoot, string sessionPath, out string error)
    {
        error = string.Empty;
        string engine = Path.Combine(appRoot, "ETS-Assistant-Engine.exe");
        string player = Path.Combine(appRoot, "tools", "CablePlayer", "CablePlayer.exe");
        if (!File.Exists(engine))
        {
            error = $"缺少后台引擎：{engine}";
            return false;
        }
        if (!File.Exists(player))
        {
            error = $"缺少音频播放工具：{player}";
            return false;
        }
        if (!File.Exists(sessionPath))
        {
            error = $"找不到口语会话：{sessionPath}";
            return false;
        }

        string logPath = Path.Combine(dataRoot, "ets_speaking_auto.log");
        int existingLineCount = 0;
        try
        {
            Directory.CreateDirectory(dataRoot);
            if (File.Exists(logPath)) existingLineCount = File.ReadAllLines(logPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"无法准备自动口语日志：{ex.Message}";
            return false;
        }

        var startInfo = new ProcessStartInfo(engine)
        {
            WorkingDirectory = appRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--speaking-auto");
        startInfo.ArgumentList.Add("--session");
        startInfo.ArgumentList.Add(sessionPath);
        startInfo.ArgumentList.Add("--data-root");
        startInfo.ArgumentList.Add(dataRoot);
        startInfo.ArgumentList.Add("--log");
        startInfo.ArgumentList.Add(logPath);
        startInfo.ArgumentList.Add("--player");
        startInfo.ArgumentList.Add(player);

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                error = "无法启动自动口语引擎。";
                return false;
            }

            lock (_sync)
            {
                _process = process;
                _logPath = logPath;
                _readLineCount = existingLineCount;
                _logTimer = new System.Threading.Timer(_ => ReadNewLogLines(), null, 0, 300);
            }
            process.Exited += (_, _) => HandleExited(process);
            process.EnableRaisingEvents = true;
            if (process.HasExited) HandleExited(process);
            return true;
        }
        catch (Exception ex)
        {
            process.Dispose();
            error = ex.Message;
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            Process? process;
            lock (_sync) process = _process;
            if (process is { HasExited: false }) process.Kill(true);
        }
        catch (Exception)
        {
        }
    }

    private void ReadNewLogLines()
    {
        string[] newLines;
        lock (_sync)
        {
            if (_logPath is null) return;
            string[] lines;
            try { lines = File.ReadAllLines(_logPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            if (lines.Length < _readLineCount) _readLineCount = 0;
            newLines = lines.Skip(_readLineCount).ToArray();
            _readLineCount = lines.Length;
        }
        foreach (string line in newLines)
            if (!string.IsNullOrWhiteSpace(line)) Output?.Invoke(line.Trim());
    }

    private void HandleExited(Process process)
    {
        ReadNewLogLines();
        lock (_sync)
        {
            if (!ReferenceEquals(_process, process)) return;
            _process = null;
            _logTimer?.Dispose();
            _logTimer = null;
        }
        Exited?.Invoke();
    }
}
