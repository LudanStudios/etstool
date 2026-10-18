using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ETSAssistant.WinUI3.Services;

public sealed partial class AssistantService
{
    private string? FindEtsExecutable()
    {
        var candidates = new List<string>();
        string? x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string? x64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(x86)) candidates.Add(Path.Combine(x86, "ETS", "Ets.exe"));
        if (!string.IsNullOrWhiteSpace(x64)) candidates.Add(Path.Combine(x64, "ETS", "Ets.exe"));

        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                foreach (string name in new[] { "Ets.exe", "ETSShell.exe" })
                {
                    using var appPath = baseKey.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{name}");
                    if (appPath?.GetValue(null) is string value) candidates.Add(ExtractExePath(value));
                }
                using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (string subName in uninstall.GetSubKeyNames())
                {
                    using var item = uninstall.OpenSubKey(subName);
                    string display = item?.GetValue("DisplayName") as string ?? "";
                    if (!display.Contains("ETS", StringComparison.OrdinalIgnoreCase) && !display.Contains("e听说", StringComparison.OrdinalIgnoreCase)) continue;
                    string? location = item?.GetValue("InstallLocation") as string;
                    string? icon = item?.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrWhiteSpace(location))
                    {
                        candidates.Add(Path.Combine(location, "Ets.exe"));
                        candidates.Add(Path.Combine(location, "ETSShell.exe"));
                    }
                    if (!string.IsNullOrWhiteSpace(icon)) candidates.Add(ExtractExePath(icon));
                }
            }
            catch { }
        }

        foreach (string raw in candidates.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            string path = ExtractExePath(raw);
            if (!File.Exists(path)) continue;
            if (Path.GetFileName(path).Equals("ETSShell.exe", StringComparison.OrdinalIgnoreCase))
            {
                string launcher = Path.Combine(Path.GetDirectoryName(path)!, "Ets.exe");
                if (File.Exists(launcher)) return Path.GetFullPath(launcher);
            }
            if (Path.GetFileName(path).Equals("Ets.exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(path);
        }
        return null;
    }

    private static string ExtractExePath(string value)
    {
        string raw = value.Trim().Trim('"');
        Match quoted = Regex.Match(raw, "^\\s*\"([^\"]+\\.exe)\"", RegexOptions.IgnoreCase);
        if (quoted.Success) return quoted.Groups[1].Value;
        int end = raw.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return end >= 0 ? raw[..(end + 4)].Trim().Trim('"') : raw;
    }
}
