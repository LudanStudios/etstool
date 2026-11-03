using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ETSAssistant.WinUI3.Services;

/// <summary>
/// Writes application diagnostics outside the user's ETS data and installation directories.
/// INFO entries are opt-in; warnings and higher severity entries are always retained.
/// </summary>
public static class DiagnosticLogService
{
    private const long MaxLogFileBytes = 10 * 1024 * 1024;
    private static readonly object WriteLock = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> TrackedControls = new();
    private static readonly object TrackedMarker = new();
    private static readonly ConditionalWeakTable<FrameworkElement, TrackingRootState> TrackingRoots = new();
    private static readonly Regex SecretAssignment = new(
        "(?i)(api[_ -]?key|access[_ -]?key(?:[_ -]?(?:id|secret))?|app[_ -]?key|authorization|token|secret)\\s*[:=]\\s*[^\\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DeepSeekKey = new("(?i)\\bsk-[a-z0-9_-]{8,}\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static int _verboseEnabled;
    private static bool _retentionApplied;

    private sealed class TrackingRootState
    {
        public int Attached;
        public int ScanQueued;
    }

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ETS Assistant", "Logs");

    public static void SetVerboseEnabled(bool enabled) => Volatile.Write(ref _verboseEnabled, enabled ? 1 : 0);

    public static void Info(string eventName, string? detail = null) => Write("INFO", eventName, detail, verboseOnly: true);
    public static void Warn(string eventName, string? detail = null) => Write("WARN", eventName, detail);
    public static void Error(string eventName, string? detail = null) => Write("ERROR", eventName, detail);
    public static void Fatal(string eventName, string? detail = null) => Write("FATAL", eventName, detail);

    public static void Error(string eventName, Exception exception) =>
        Error(eventName, FormatException(exception, includeStack: false));

    public static void Fatal(string eventName, Exception exception) =>
        Fatal(eventName, FormatException(exception, includeStack: true));

    public static async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, string eventName = "ui.dialog")
    {
        string rawTitle = dialog.Title?.ToString() ?? string.Empty;
        string title = string.IsNullOrWhiteSpace(rawTitle) ? "untitled" : Clean(rawTitle, 100);
        Info($"{eventName}.opened", $"title=\"{title}\"");
        try
        {
            ContentDialogResult result = await dialog.ShowAsync();
            Info($"{eventName}.closed", $"result={result}");
            return result;
        }
        catch (Exception ex)
        {
            Error($"{eventName}.failed", ex);
            throw;
        }
    }

    public static IReadOnlyList<string> GetLogFiles()
    {
        try
        {
            return Directory.Exists(LogDirectory)
                ? Directory.EnumerateFiles(LogDirectory, "ets-diagnostics-*.log*")
                    .OrderByDescending(File.GetLastWriteTimeUtc).ToArray()
                : Array.Empty<string>();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    public static string ReadRecentEntries(int maxLines = 240)
    {
        var lines = new List<string>();
        foreach (string file in GetLogFiles().Take(7).Reverse())
        {
            try { lines.AddRange(File.ReadLines(file, Encoding.UTF8).TakeLast(maxLines)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return string.Join(Environment.NewLine, lines.TakeLast(maxLines));
    }

    public static void ClearLogs()
    {
        try
        {
            foreach (string file in GetLogFiles())
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Warn("diagnostics.clear.failed", $"file={Path.GetFileName(file)} detail={ex.Message}");
                }
            }
        }
        catch (Exception ex) { Warn("diagnostics.clear.failed", ex.Message); }
    }

    public static void TrackInteractiveControls(FrameworkElement root, string pageName)
    {
        try
        {
            TrackingRootState state = TrackingRoots.GetValue(root, static _ => new TrackingRootState());
            if (Interlocked.Exchange(ref state.Attached, 1) == 0)
            {
                root.LayoutUpdated += (_, _) =>
                {
                    if (Interlocked.Exchange(ref state.ScanQueued, 1) != 0) return;
                    if (!root.DispatcherQueue.TryEnqueue(() =>
                        {
                            Volatile.Write(ref state.ScanQueued, 0);
                            TrackRecursive(root, pageName);
                        }))
                        Volatile.Write(ref state.ScanQueued, 0);
                };
            }
            TrackRecursive(root, pageName);
        }
        catch (Exception ex) { Warn("diagnostics.ui_tracking.failed", $"page={pageName} detail={ex.Message}"); }
    }

    public static void TrackWindowButtons(FrameworkElement root, string windowName)
    {
        TrackInteractiveControls(root, windowName);
    }

    public static void LogException(string eventName, Exception exception, bool fatal = false)
    {
        if (fatal) Fatal(eventName, exception);
        else Error(eventName, exception);
    }

    private static void TrackRecursive(DependencyObject current, string pageName)
    {
        if (current is FrameworkElement element)
        {
            if (!TrackedControls.TryGetValue(element, out _))
            {
                TrackedControls.Add(element, TrackedMarker);
                string control = GetControlIdentifier(element);
                if (element is ButtonBase button)
                    button.Click += (_, _) => Info($"{pageName}.button.pressed", $"button=\"{GetButtonLabel(button)}\"");
                else if (element is ToggleSwitch toggle)
                    toggle.Toggled += (_, _) => Info("ui.control.changed", $"page={pageName} control={control} value={toggle.IsOn}");
                else if (element is CheckBox checkBox)
                {
                    checkBox.Checked += (_, _) => Info("ui.control.changed", $"page={pageName} control={control} value=true");
                    checkBox.Unchecked += (_, _) => Info("ui.control.changed", $"page={pageName} control={control} value=false");
                }
                else if (element is ComboBox comboBox)
                    comboBox.SelectionChanged += (_, _) => Info("ui.control.changed", $"page={pageName} control={control} selectedIndex={comboBox.SelectedIndex}");
                else if (element is Slider slider)
                    slider.ValueChanged += (_, _) => Info("ui.control.changed", $"page={pageName} control={control} value={slider.Value:0.###}");
                else if (element is NumberBox numberBox)
                    numberBox.ValueChanged += (_, _) => Info("ui.control.changed", $"page={pageName} control={control} value={numberBox.Value:0.###}");
                else if (element is TextBox or PasswordBox)
                    element.GotFocus += (_, _) => Info("ui.input.focused", $"page={pageName} control={control}");
            }
        }

        int count = VisualTreeHelper.GetChildrenCount(current);
        for (int index = 0; index < count; index++)
            TrackRecursive(VisualTreeHelper.GetChild(current, index), pageName);
    }

    private static string GetButtonLabel(ButtonBase button)
    {
        string automationName = AutomationProperties.GetName(button);
        if (!string.IsNullOrWhiteSpace(automationName)) return Clean(automationName, 100);
        if (!string.IsNullOrWhiteSpace(button.Name)) return Clean(button.Name, 100);
        var parts = new List<string>();
        if (button is ContentControl contentControl)
            CollectText(contentControl.Content as DependencyObject, parts, 4);
        return parts.Count == 0 ? button.GetType().Name : Clean(string.Join(" ", parts.Distinct()), 100);
    }

    private static void CollectText(DependencyObject? current, List<string> parts, int depth)
    {
        if (current is null || depth < 0) return;
        if (current is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
        {
            parts.Add(textBlock.Text);
            return;
        }
        int count = VisualTreeHelper.GetChildrenCount(current);
        for (int index = 0; index < count; index++)
            CollectText(VisualTreeHelper.GetChild(current, index), parts, depth - 1);
    }

    private static string GetControlIdentifier(FrameworkElement element)
    {
        if (!string.IsNullOrWhiteSpace(element.Name)) return Clean(element.Name, 80);
        string automationName = AutomationProperties.GetName(element);
        if (!string.IsNullOrWhiteSpace(automationName)) return Clean(automationName, 80);
        return element.GetType().Name;
    }

    private static string FormatException(Exception exception, bool includeStack)
    {
        string detail = $"{exception.GetType().Name}: {exception.Message}";
        if (includeStack && !string.IsNullOrWhiteSpace(exception.StackTrace))
            detail += $" stack={exception.StackTrace}";
        return Clean(detail, includeStack ? 3000 : 600);
    }

    private static void Write(string level, string eventName, string? detail, bool verboseOnly = false)
    {
        if (verboseOnly && Volatile.Read(ref _verboseEnabled) == 0) return;
        try
        {
            string date = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            string userName = Clean(Environment.UserName, 80).Replace(' ', '_');
            string eventKey = Clean(eventName, 120);
            string safeDetail = string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + Clean(detail, 3000);
            string path = Path.Combine(LogDirectory, $"ets-diagnostics-{date}.log");
            lock (WriteLock)
            {
                Directory.CreateDirectory(LogDirectory);
                ApplyRetentionOnce();
                if (File.Exists(path) && new FileInfo(path).Length >= MaxLogFileBytes)
                {
                    string rotated = path + ".1";
                    if (File.Exists(rotated)) File.Delete(rotated);
                    File.Move(path, rotated);
                }
                File.AppendAllText(path, $"[{level}] {timestamp}_{userName} | {eventKey}{safeDetail}{Environment.NewLine}", new UTF8Encoding(false));
            }
        }
        catch (Exception) { /* Logging must never interrupt the application operation. */ }
    }

    private static string Clean(string value, int maxLength)
    {
        string safe = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        safe = SecretAssignment.Replace(safe, "$1=[REDACTED]");
        safe = DeepSeekKey.Replace(safe, "[REDACTED_API_KEY]");
        safe = string.Join(' ', safe.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return safe.Length <= maxLength ? safe : safe[..maxLength] + "…";
    }

    private static void ApplyRetentionOnce()
    {
        if (_retentionApplied) return;
        _retentionApplied = true;
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-30);
            foreach (string file in Directory.EnumerateFiles(LogDirectory, "ets-diagnostics-*.log*"))
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
        }
        catch (Exception) { }
    }
}
