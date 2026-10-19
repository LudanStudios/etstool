using System.Text.Json;
using Microsoft.UI.Xaml;

namespace ETSAssistant.WinUI3.Services;

public sealed class AppPreferencesService
{
    private static readonly Lazy<AppPreferencesService> Lazy = new(() => new AppPreferencesService());
    public static AppPreferencesService Current => Lazy.Value;

    private readonly string _path;

    private AppPreferencesService()
    {
        _path = Path.Combine(AssistantService.Current.DataRoot, "ui_preferences.json");
        Load();
    }

    public string Theme { get; private set; } = "System";
    public string Language { get; private set; } = "zh-CN";
    public string SpeakingMode { get; private set; } = "standard";
    public event Action? ThemeChanged;
    public event Action? LanguageChanged;
    public event Action? SpeakingModeChanged;

    public void SetTheme(string theme)
    {
        if (theme is not ("System" or "Light" or "Dark") || theme == Theme) return;
        Theme = theme;
        Save();
        ThemeChanged?.Invoke();
    }

    public void SetLanguage(string language)
    {
        language = language == "en-US" ? "en-US" : "zh-CN";
        if (language == Language) return;
        Language = language;
        Save();
        LocalizationService.Current.Load(language);
        LanguageChanged?.Invoke();
    }

    public void SetSpeakingMode(string mode)
    {
        mode = mode == "flexible" ? "flexible" : "standard";
        if (mode == SpeakingMode) return;
        SpeakingMode = mode;
        Save();
        SpeakingModeChanged?.Invoke();
    }

    public void ApplyTheme(FrameworkElement root)
    {
        root.RequestedTheme = Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_path));
            if (document.RootElement.TryGetProperty("theme", out JsonElement theme) &&
                theme.ValueKind == JsonValueKind.String)
                Theme = theme.GetString() is "Light" or "Dark" ? theme.GetString()! : "System";
            if (document.RootElement.TryGetProperty("language", out JsonElement language) &&
                language.ValueKind == JsonValueKind.String)
                Language = language.GetString() == "en-US" ? "en-US" : "zh-CN";
            if (document.RootElement.TryGetProperty("speaking_mode", out JsonElement speakingMode) &&
                speakingMode.ValueKind == JsonValueKind.String)
                SpeakingMode = speakingMode.GetString() == "flexible" ? "flexible" : "standard";
        }
        catch (Exception) { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new
            {
                theme = Theme,
                language = Language,
                speaking_mode = SpeakingMode
            }));
        }
        catch (Exception) { }
    }
}
