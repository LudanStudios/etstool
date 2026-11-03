using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ETSAssistant.WinUI3.Services;

public sealed record LocalizationLanguage(string Code, string DisplayName);

/// <summary>Loads localized values from the semantic-key JSON files in the locales folder.</summary>
public sealed class LocalizationService
{
    private static readonly Lazy<LocalizationService> Lazy = new(() => new LocalizationService());
    private static readonly System.Text.RegularExpressions.Regex LocaleCodePattern =
        new(@"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    public static LocalizationService Current => Lazy.Value;

    private Dictionary<string, Dictionary<string, string>> _locales = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _sourceLocale = new(StringComparer.Ordinal);
    private Dictionary<string, string> _currentLocale = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<DependencyObject, Dictionary<string, ElementLocalization>> _elementLocalizations = new();
    private readonly Dictionary<string, LocalizedToken> _renderedTokens = new(StringComparer.Ordinal);
    private string _localeFingerprint = string.Empty;
    public string Language { get; private set; } = "zh-CN";

    private sealed record LocalizedToken(string Key, (string Name, object? Value)[]? Values = null);

    private sealed record ElementLocalization(LocalizedToken Token, string LastRendered);

    public static IReadOnlyList<LocalizationLanguage> GetAvailableLanguages()
    {
        string directory = GetLocaleDirectory();
        if (!Directory.Exists(directory)) return Array.Empty<LocalizationLanguage>();

        var languages = new List<LocalizationLanguage>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            string code = Path.GetFileNameWithoutExtension(path);
            if (!LocaleCodePattern.IsMatch(code)) continue;

            try
            {
                _ = CultureInfo.GetCultureInfo(code);
                string displayName = ReadLocaleDisplayName(path) ?? code;
                languages.Add(new LocalizationLanguage(code, displayName));
            }
            catch (CultureNotFoundException)
            {
                // Files such as translation templates are not selectable locales.
            }
        }

        return languages
            .OrderBy(language => language.Code.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(language => language.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string? ReadLocaleDisplayName(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("_meta", out JsonElement metadata) ||
                metadata.ValueKind != JsonValueKind.Object ||
                !metadata.TryGetProperty("displayName", out JsonElement displayName) ||
                displayName.ValueKind != JsonValueKind.String)
                return null;

            string? value = displayName.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsAvailableLanguage(string? language) =>
        !string.IsNullOrWhiteSpace(language) &&
        GetAvailableLanguages().Any(item => item.Code.Equals(language, StringComparison.OrdinalIgnoreCase));

    public void Load(string language)
    {
        var loaded = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (LocalizationLanguage item in GetAvailableLanguages())
        {
            string path = Path.Combine(GetLocaleDirectory(), item.Code + ".json");
            Dictionary<string, string> locale = ReadLocale(path);
            if (locale.Count > 0) loaded[item.Code] = locale;
        }

        _locales = loaded;
        if (!_locales.ContainsKey("zh-CN") && _locales.Count > 0)
            _sourceLocale = _locales.Values.First();
        else
            _sourceLocale = _locales.GetValueOrDefault("zh-CN") ?? new Dictionary<string, string>(StringComparer.Ordinal);

        Language = _locales.ContainsKey(language) ? language :
            _locales.ContainsKey("zh-CN") ? "zh-CN" : _locales.Keys.FirstOrDefault() ?? "zh-CN";
        _currentLocale = _locales.GetValueOrDefault(Language) ?? _sourceLocale;
        _localeFingerprint = GetLocaleFingerprint();
    }

    /// <summary>Resolves a semantic localization key. UI text is never reverse-matched from Chinese source strings.</summary>
    public string Translate(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        var token = new LocalizedToken(key);
        string rendered = Render(token);
        RegisterRenderedToken(rendered, token);
        return rendered;
    }

    public string Format(string key, params (string Name, object? Value)[] values)
    {
        var token = new LocalizedToken(key, values?.ToArray() ?? Array.Empty<(string Name, object? Value)>());
        string rendered = Render(token);
        RegisterRenderedToken(rendered, token);
        return rendered;
    }

    /// <summary>Reloads edited locale JSON files without restarting the app.</summary>
    public bool ReloadIfChanged()
    {
        string fingerprint = GetLocaleFingerprint();
        if (string.Equals(fingerprint, _localeFingerprint, StringComparison.Ordinal)) return false;
        Load(Language);
        return true;
    }

    private string Render(LocalizedToken token, int depth = 0)
    {
        string rendered = GetLocalizedValue(token.Key, token.Key);
        if (token.Values is null) return rendered;

        foreach ((string name, object? value) in token.Values)
        {
            string replacement = FormatValue(value, depth + 1);
            rendered = rendered.Replace("{" + name + "}", replacement, StringComparison.Ordinal);
        }
        return rendered;
    }

    private string FormatValue(object? value, int depth)
    {
        if (depth < 6 && value is string text && _renderedTokens.TryGetValue(text, out LocalizedToken? nested))
            return Render(nested, depth + 1);
        return Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
    }

    private void RegisterRenderedToken(string rendered, LocalizedToken token)
    {
        if (string.IsNullOrEmpty(rendered)) return;
        if (_renderedTokens.Count > 8192) _renderedTokens.Clear();
        _renderedTokens[rendered] = token;
    }

    private string TranslateElementValue(DependencyObject owner, string property, string value)
    {
        Dictionary<string, ElementLocalization> properties = _elementLocalizations.GetOrCreateValue(owner);
        LocalizedToken? token = null;
        if (_sourceLocale.ContainsKey(value) || _currentLocale.ContainsKey(value))
            token = new LocalizedToken(value);
        else if (_renderedTokens.TryGetValue(value, out LocalizedToken? renderedToken))
            token = renderedToken;
        else if (properties.TryGetValue(property, out ElementLocalization? previous) &&
                 string.Equals(previous.LastRendered, value, StringComparison.Ordinal))
            token = previous.Token;

        if (token is null)
        {
            properties.Remove(property);
            return value;
        }

        string translated = Render(token);
        properties[property] = new ElementLocalization(token, translated);
        RegisterRenderedToken(translated, token);
        return translated;
    }

    public void Apply(DependencyObject? root)
    {
        Apply(root, new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance));
    }

    private void Apply(DependencyObject? root, HashSet<DependencyObject> visited)
    {
        if (root is null || !visited.Add(root)) return;
        switch (root)
        {
            case TextBlock block when !string.IsNullOrWhiteSpace(block.Text):
                block.Text = TranslateElementValue(block, "Text", block.Text);
                break;
            case NavigationViewItem navigationItem when navigationItem.Content is string itemText:
                navigationItem.Content = TranslateElementValue(navigationItem, "Content", itemText);
                break;
            case ComboBoxItem comboItem when comboItem.Content is string comboText:
                comboItem.Content = TranslateElementValue(comboItem, "Content", comboText);
                break;
            case ComboBox combo:
                foreach (object item in combo.Items)
                    if (item is ComboBoxItem { Content: string content } comboBoxItem)
                        comboBoxItem.Content = TranslateElementValue(comboBoxItem, "Content", content);
                break;
            case Button button when button.Content is string buttonText:
                button.Content = TranslateElementValue(button, "Content", buttonText);
                break;
            case HyperlinkButton link when link.Content is string linkText:
                link.Content = TranslateElementValue(link, "Content", linkText);
                break;
            case ContentDialog dialog:
                if (dialog.Title is string dialogTitle && !string.IsNullOrWhiteSpace(dialogTitle))
                    dialog.Title = TranslateElementValue(dialog, "Title", dialogTitle);
                dialog.PrimaryButtonText = TranslateElementValue(dialog, "PrimaryButtonText", dialog.PrimaryButtonText);
                dialog.SecondaryButtonText = TranslateElementValue(dialog, "SecondaryButtonText", dialog.SecondaryButtonText);
                dialog.CloseButtonText = TranslateElementValue(dialog, "CloseButtonText", dialog.CloseButtonText);
                if (dialog.Content is DependencyObject dialogContent) Apply(dialogContent, visited);
                else if (dialog.Content is string dialogText)
                    dialog.Content = TranslateElementValue(dialog, "Content", dialogText);
                break;
            case TextBox textBox:
                if (!string.IsNullOrWhiteSpace(textBox.PlaceholderText))
                    textBox.PlaceholderText = TranslateElementValue(textBox, "PlaceholderText", textBox.PlaceholderText);
                if (textBox.Header is string textHeader)
                    textBox.Header = TranslateElementValue(textBox, "Header", textHeader);
                break;
            case PasswordBox passwordBox:
                if (!string.IsNullOrWhiteSpace(passwordBox.PlaceholderText))
                    passwordBox.PlaceholderText = TranslateElementValue(passwordBox, "PlaceholderText", passwordBox.PlaceholderText);
                if (passwordBox.Header is string passwordHeader)
                    passwordBox.Header = TranslateElementValue(passwordBox, "Header", passwordHeader);
                break;
            case CheckBox checkBox when checkBox.Content is string checkText:
                checkBox.Content = TranslateElementValue(checkBox, "Content", checkText);
                break;
            case ContentControl contentControl when contentControl.Content is string contentText:
                contentControl.Content = TranslateElementValue(contentControl, "Content", contentText);
                break;
            case ToggleSwitch toggleSwitch:
                if (toggleSwitch.OnContent is string onText)
                    toggleSwitch.OnContent = TranslateElementValue(toggleSwitch, "OnContent", onText);
                if (toggleSwitch.OffContent is string offText)
                    toggleSwitch.OffContent = TranslateElementValue(toggleSwitch, "OffContent", offText);
                break;
            case InfoBar infoBar:
                if (!string.IsNullOrWhiteSpace(infoBar.Title))
                    infoBar.Title = TranslateElementValue(infoBar, "Title", infoBar.Title);
                if (!string.IsNullOrWhiteSpace(infoBar.Message))
                    infoBar.Message = TranslateElementValue(infoBar, "Message", infoBar.Message);
                break;
        }

        TranslateStringProperty(root, "Header");
        TranslateStringProperty(root, "PlaceholderText");
        if (ToolTipService.GetToolTip(root) is string toolTip)
            ToolTipService.SetToolTip(root, TranslateElementValue(root, "ToolTip", toolTip));
        if (AutomationProperties.GetName(root) is string automationName)
            AutomationProperties.SetName(root, TranslateElementValue(root, "AutomationName", automationName));

        // XAML elements nested inside Button/ContentControl content aren't always
        // exposed through VisualTreeHelper when the control template is deferred.
        // Walk the content and panel trees explicitly so icon + text buttons are localized.
        if (root is ContentControl { Content: DependencyObject nestedContent })
            Apply(nestedContent, visited);
        if (root is Panel panel)
            foreach (UIElement child in panel.Children)
                Apply(child, visited);
        if (root is ItemsControl itemsControl)
            foreach (object item in itemsControl.Items)
                if (item is DependencyObject itemElement)
                    Apply(itemElement, visited);

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
            Apply(VisualTreeHelper.GetChild(root, index), visited);
    }

    private static void TranslateStringProperty(DependencyObject root, string propertyName)
    {
        var property = root.GetType().GetProperty(propertyName);
        if (property?.CanRead != true || !property.CanWrite) return;
        if (property.GetValue(root) is string value && !string.IsNullOrWhiteSpace(value))
            property.SetValue(root, Current.TranslateElementValue(root, propertyName, value));
    }

    private string GetLocalizedValue(string key, string fallback) =>
        _currentLocale.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value :
        _sourceLocale.TryGetValue(key, out string? sourceValue) && !string.IsNullOrWhiteSpace(sourceValue)
            ? sourceValue : fallback;

    private static Dictionary<string, string> ReadLocale(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string>(StringComparer.Ordinal);

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String)
                    values[property.Name] = property.Value.GetString() ?? string.Empty;
            return values;
        }
        catch (Exception)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string GetLocaleFingerprint()
    {
        string directory = GetLocaleDirectory();
        if (!Directory.Exists(directory)) return string.Empty;

        var fingerprint = new System.Text.StringBuilder();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var file = new FileInfo(path);
                fingerprint.Append(file.Name).Append('|')
                    .Append(file.Length).Append('|')
                    .Append(file.LastWriteTimeUtc.Ticks).Append('\n');
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return fingerprint.ToString();
    }

    private static string GetLocaleDirectory() => Path.Combine(AppContext.BaseDirectory, "locales");

}
