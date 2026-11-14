using System.Text.Json;
using Microsoft.UI.Xaml;

namespace ETSAssistant.WinUI3.Services;

public sealed class AppPreferencesService
{
    // 手工开关：仅内部测试时设为 true；正式版保持 false。
    private const bool InternalTestNoUpdates = false;

    private static readonly Lazy<AppPreferencesService> Lazy = new(() => new AppPreferencesService());
    public static AppPreferencesService Current => Lazy.Value;

    private readonly string _path;

    private AppPreferencesService()
    {
        _path = Path.Combine(AssistantService.Current.DataRoot, "ui_preferences.json");
        Load();
        DiagnosticLogService.SetVerboseEnabled(VerboseLoggingEnabled);
    }

    public string Theme { get; private set; } = "System";
    public string Language { get; private set; } = "zh-CN";
    public string SpeakingMode { get; private set; } = "standard";
    public bool CredentialsReminderShown { get; private set; }
    public bool VirtualCableReminderShown { get; private set; }
    public bool WelcomeCompleted { get; private set; }
    public bool AutoUpdateEnabled { get; private set; } = true;
    public bool MinimizeToTrayEnabled { get; private set; }
    public bool InternalTestNoUpdatesEnabled => InternalTestNoUpdates;
    public bool VerboseLoggingEnabled { get; private set; }
    public bool DeveloperToolsUnlocked { get; private set; }
    public bool AutomaticSpeakingEnabled { get; private set; } = true;
    public bool AutomaticSelectionEnabled => !File.Exists(SelectionDisabledPath);
    private string SelectionDisabledPath => Path.Combine(AssistantService.Current.DataRoot, "_assistant", "automatic_selection.disabled");
    public event Action? AutomationChanged;

    public void SetAutomaticSelection(bool enabled)
    {
        if (enabled)
        {
            // Start the listener before releasing the disabled marker so a
            // failed startup leaves selection safely disabled.
            AssistantService.Current.EnsureListener();
            File.Delete(SelectionDisabledPath);
        }
        else SettingsFileStore.Write(SelectionDisabledPath, "disabled\n");
        AutomationChanged?.Invoke();
    }

    public void SetAutomaticSpeaking(bool enabled)
    {
        if (AutomaticSpeakingEnabled == enabled) return;
        AutomaticSpeakingEnabled = enabled;
        Save();
        AutomationChanged?.Invoke();
    }

    public DateTimeOffset? LastUpdateCheckUtc { get; private set; }
    public event Action? ThemeChanged;
    public event Action? LanguageChanged;
    public event Action? SpeakingModeChanged;
    public event Action? MinimizeToTrayChanged;

    public void SetTheme(string theme)
    {
        if (theme is not ("System" or "Light" or "Dark") || theme == Theme) return;
        Theme = theme;
        Save();
        ThemeChanged?.Invoke();
    }

    public void SetLanguage(string language)
    {
        LocalizationLanguage? locale = LocalizationService.GetAvailableLanguages()
            .FirstOrDefault(item => item.Code.Equals(language, StringComparison.OrdinalIgnoreCase));
        if (locale is null) return;
        language = locale.Code;
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

    public void SetAutoUpdate(bool enabled)
    {
        if (InternalTestNoUpdatesEnabled) return;
        if (AutoUpdateEnabled == enabled) return;
        AutoUpdateEnabled = enabled;
        Save();
    }

    public void SetMinimizeToTray(bool enabled)
    {
        if (MinimizeToTrayEnabled == enabled) return;
        MinimizeToTrayEnabled = enabled;
        Save();
        DiagnosticLogService.Info("settings.minimize_to_tray.changed", $"enabled={enabled}");
        MinimizeToTrayChanged?.Invoke();
    }

    public void SetVerboseLogging(bool enabled)
    {
        if (VerboseLoggingEnabled == enabled) return;
        VerboseLoggingEnabled = enabled;
        DiagnosticLogService.SetVerboseEnabled(enabled);
        Save();
        DiagnosticLogService.Info("settings.verbose_logging.enabled", "source=developer_tools");
        AssistantService.Current.SyncListenerSettings();
    }

    public void MarkWelcomeCompleted()
    {
        if (WelcomeCompleted) return;
        WelcomeCompleted = true;
        Save();
    }

    public void UnlockDeveloperTools()
    {
        if (DeveloperToolsUnlocked) return;
        DeveloperToolsUnlocked = true;
        Save();
    }

    public void MarkUpdateCheck(DateTimeOffset checkedAt)
    {
        LastUpdateCheckUtc = checkedAt;
        Save();
    }

    public void MarkCredentialsReminderShown()
    {
        if (CredentialsReminderShown) return;
        CredentialsReminderShown = true;
        Save();
    }

    public void MarkVirtualCableReminderShown()
    {
        if (VirtualCableReminderShown) return;
        VirtualCableReminderShown = true;
        Save();
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
            {
                string? savedLanguage = language.GetString();
                LocalizationLanguage? locale = LocalizationService.GetAvailableLanguages()
                    .FirstOrDefault(item => item.Code.Equals(savedLanguage, StringComparison.OrdinalIgnoreCase));
                if (locale is not null) Language = locale.Code;
            }
            if (document.RootElement.TryGetProperty("speaking_mode", out JsonElement speakingMode) &&
                speakingMode.ValueKind == JsonValueKind.String)
                SpeakingMode = speakingMode.GetString() == "flexible" ? "flexible" : "standard";
            if (document.RootElement.TryGetProperty("credentials_reminder_shown", out JsonElement reminder) &&
                reminder.ValueKind is JsonValueKind.True or JsonValueKind.False)
                CredentialsReminderShown = reminder.GetBoolean();
            if (document.RootElement.TryGetProperty("virtual_cable_reminder_shown", out JsonElement cableReminder) &&
                cableReminder.ValueKind is JsonValueKind.True or JsonValueKind.False)
                VirtualCableReminderShown = cableReminder.GetBoolean();
            WelcomeCompleted = ReadBool(document.RootElement, "welcome_completed");
            AutoUpdateEnabled = !document.RootElement.TryGetProperty("auto_update_enabled", out JsonElement update) ||
                                update.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || update.GetBoolean();
            MinimizeToTrayEnabled = ReadBool(document.RootElement, "minimize_to_tray_enabled");
            VerboseLoggingEnabled = ReadBool(document.RootElement, "verbose_logging_enabled");
            DeveloperToolsUnlocked = ReadBool(document.RootElement, "developer_tools_unlocked");
            AutomaticSpeakingEnabled = !document.RootElement.TryGetProperty("automatic_speaking_enabled", out JsonElement speakingEnabled) ||
                speakingEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || speakingEnabled.GetBoolean();
            if (document.RootElement.TryGetProperty("last_update_check_utc", out JsonElement lastCheck) &&
                lastCheck.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(lastCheck.GetString(), out DateTimeOffset parsedCheck))
                LastUpdateCheckUtc = parsedCheck;
        }
        catch (Exception) { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            SettingsFileStore.Write(_path, JsonSerializer.Serialize(new
            {
                theme = Theme,
                language = Language,
                speaking_mode = SpeakingMode,
                credentials_reminder_shown = CredentialsReminderShown,
                virtual_cable_reminder_shown = VirtualCableReminderShown,
                welcome_completed = WelcomeCompleted,
                auto_update_enabled = AutoUpdateEnabled,
                minimize_to_tray_enabled = MinimizeToTrayEnabled,
                verbose_logging_enabled = VerboseLoggingEnabled,
                developer_tools_unlocked = DeveloperToolsUnlocked,
                automatic_speaking_enabled = AutomaticSpeakingEnabled,
                last_update_check_utc = LastUpdateCheckUtc?.ToString("O")
            }));
        }
        catch (Exception ex) { DiagnosticLogService.Warn("settings.preferences.save_failed", ex.Message); }
    }

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
