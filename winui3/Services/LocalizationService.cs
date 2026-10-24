using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ETSAssistant.WinUI3.Services;

/// <summary>Lightweight text localizer. English locale maps the Chinese source strings used by XAML.</summary>
public sealed class LocalizationService
{
    private static readonly Lazy<LocalizationService> Lazy = new(() => new LocalizationService());
    public static LocalizationService Current => Lazy.Value;

    private Dictionary<string, string> _zhToEn = new(StringComparer.Ordinal);
    private Dictionary<string, string> _enToZh = new(StringComparer.Ordinal);

    public string Language { get; private set; } = "zh-CN";

    private static readonly (string Chinese, string English)[] Prefixes =
    {
        ("日志目录：", "Log folder: "),
        ("时间：", "Time: "),
        ("应用目录：", "App folder: "),
        ("数据目录：", "Data folder: "),
        ("ETS 程序：", "ETS executable: "),
        ("后台引擎：", "Engine: "),
        ("系统：", "System: "),
        ("抓包文件：", "Capture file: "),
        ("读取体验模式用量失败：", "Could not read Experience Mode usage: ")
    };

    public void Load(string language)
    {
        Language = language == "en-US" ? "en-US" : "zh-CN";
        _zhToEn.Clear();
        _enToZh.Clear();
        if (Language == "zh-CN") return;

        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Locales", "en-US.json");
            if (!File.Exists(path)) return;
            _zhToEn = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                      ?? new Dictionary<string, string>(StringComparer.Ordinal);
            _enToZh = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach ((string chinese, string english) in _zhToEn)
                _enToZh.TryAdd(english, chinese);
        }
        catch (Exception) { }
    }

    public string Translate(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (Language == "en-US")
        {
            if (_zhToEn.TryGetValue(text, out string? english)) return english;

            foreach ((string prefix, string replacement) in Prefixes)
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                    return replacement + text[prefix.Length..];

            Match match = Regex.Match(text, @"^试卷 (\d+)$");
            if (match.Success) return $"Paper {match.Groups[1].Value}";
            match = Regex.Match(text, @"^试卷编号 (\d+) · 共 (\d+) 道选择题(?: · 最后作答 (\d{2}:\d{2}:\d{2}))?$");
            if (match.Success)
                return $"Paper ID {match.Groups[1].Value} · {match.Groups[2].Value} questions" +
                       (match.Groups[3].Success ? $" · Last answered {match.Groups[3].Value}" : "");
            match = Regex.Match(text, @"^已答 (\d+) / (\d+) 题$");
            if (match.Success) return $"Answered {match.Groups[1].Value} / {match.Groups[2].Value}";
            match = Regex.Match(text, @"^本地记录：答案一致 (\d+) 题(?: · 待复习 (\d+) 题)?$");
            if (match.Success)
                return $"Local records: {match.Groups[1].Value} matching answers" +
                       (match.Groups[2].Success ? $" · {match.Groups[2].Value} to review" : "");
            match = Regex.Match(text, @"^共 (\d+) 份试卷$");
            if (match.Success) return $"Total {match.Groups[1].Value} papers";
            match = Regex.Match(text, @"^试卷 (\d+)（(\d+) 道选择题）$");
            if (match.Success) return $"Paper {match.Groups[1].Value} ({match.Groups[2].Value} questions)";
            match = Regex.Match(text, @"^试卷 (\d+) 的答案（(\d+) 题）$");
            if (match.Success) return $"Answers for paper {match.Groups[1].Value} ({match.Groups[2].Value} questions)";
            match = Regex.Match(text, @"^本设备剩余 (\d+)/(\d+) 次 · 当前 IP 剩余 (\d+)/(\d+) 次$");
            if (match.Success)
                return $"This device remaining {match.Groups[1].Value}/{match.Groups[2].Value} · Current IP remaining {match.Groups[3].Value}/{match.Groups[4].Value}";
            match = Regex.Match(text, @"^刷新于：(\d{2}:\d{2}) · 北京时间 (\d{2}:\d{2}) 重置$");
            if (match.Success) return $"Updated {match.Groups[1].Value} · resets at {match.Groups[2].Value} China time";
            match = Regex.Match(text, @"^上次作答：已答 (\d+) 题 · 正确 (\d+) 题 · 错误 (\d+) 题$");
            if (match.Success)
                return $"Last attempt: answered {match.Groups[1].Value} · correct {match.Groups[2].Value} · wrong {match.Groups[3].Value}";
            return text;
        }
        return _enToZh.TryGetValue(text, out string? chinese) ? chinese : text;
    }

    public void Apply(DependencyObject? root)
    {
        if (root is null) return;
        switch (root)
        {
            case TextBlock block when !string.IsNullOrWhiteSpace(block.Text):
                block.Text = Translate(block.Text);
                break;
            case NavigationViewItem navigationItem when navigationItem.Content is string itemText:
                navigationItem.Content = Translate(itemText);
                break;
            case ComboBoxItem comboItem when comboItem.Content is string comboText:
                comboItem.Content = Translate(comboText);
                break;
            case ComboBox combo:
                foreach (object item in combo.Items)
                    if (item is ComboBoxItem { Content: string content })
                        ((ComboBoxItem)item).Content = Translate(content);
                break;
            case Button button when button.Content is string buttonText:
                button.Content = Translate(buttonText);
                break;
            case HyperlinkButton link when link.Content is string linkText:
                link.Content = Translate(linkText);
                break;
            case TextBox textBox:
                if (!string.IsNullOrWhiteSpace(textBox.PlaceholderText))
                    textBox.PlaceholderText = Translate(textBox.PlaceholderText);
                if (textBox.Header is string textHeader)
                    textBox.Header = Translate(textHeader);
                break;
            case PasswordBox passwordBox:
                if (!string.IsNullOrWhiteSpace(passwordBox.PlaceholderText))
                    passwordBox.PlaceholderText = Translate(passwordBox.PlaceholderText);
                if (passwordBox.Header is string passwordHeader)
                    passwordBox.Header = Translate(passwordHeader);
                break;
            case CheckBox checkBox when checkBox.Content is string checkText:
                checkBox.Content = Translate(checkText);
                break;
            case ToggleSwitch toggleSwitch:
                if (toggleSwitch.OnContent is string onText) toggleSwitch.OnContent = Translate(onText);
                if (toggleSwitch.OffContent is string offText) toggleSwitch.OffContent = Translate(offText);
                break;
            case InfoBar infoBar:
                if (!string.IsNullOrWhiteSpace(infoBar.Title)) infoBar.Title = Translate(infoBar.Title);
                if (!string.IsNullOrWhiteSpace(infoBar.Message)) infoBar.Message = Translate(infoBar.Message);
                break;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
            Apply(VisualTreeHelper.GetChild(root, index));
    }
}
