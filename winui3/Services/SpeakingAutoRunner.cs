using System.Diagnostics;

namespace ETSAssistant.WinUI3.Services;

/// <summary>Starts ets_speaking_auto.py with the packaged CablePlayer helper.</summary>
public sealed class SpeakingAutoRunner
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public event Action<string>? Output;
    public event Action? Exited;

    public bool Start(string appRoot, out string error)
    {
        error = string.Empty;
        string script = Path.Combine(appRoot, "ets_speaking_auto.py");
        if (!File.Exists(script))
        {
            error = $"缺少 {script}";
            return false;
        }

        string? python = ResolvePythonPath(appRoot);
        if (python is null)
        {
            error = "未找到 python.exe。自动口语目前需要本机安装 Python 3。";
            return false;
        }

        var startInfo = new ProcessStartInfo(python)
        {
            WorkingDirectory = appRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(script);
        string player = Path.Combine(appRoot, "tools", "CablePlayer", "CablePlayer.exe");
        if (File.Exists(player))
        {
            startInfo.ArgumentList.Add("--player");
            startInfo.ArgumentList.Add(player);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => Report(args.Data);
        process.ErrorDataReceived += (_, args) => Report(args.Data);
        process.Exited += (_, _) =>
        {
            _process = null;
            Exited?.Invoke();
        };
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
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
            if (_process is { HasExited: false }) _process.Kill(true);
        }
        catch (Exception)
        {
        }
    }

    private void Report(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line)) Output?.Invoke(line.Trim());
    }

    private static string? ResolvePythonPath(string root)
    {
        var candidates = new List<string>();
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (string directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                try { candidates.Add(Path.Combine(directory.Trim('"'), "python.exe")); }
                catch (ArgumentException) { }
            }
        }
        candidates.Add(Path.Combine(root, ".build", "tools", "python", "python.exe"));
        candidates.Add(@"C:\Python314\python.exe");
        return candidates.FirstOrDefault(File.Exists);
    }
}
