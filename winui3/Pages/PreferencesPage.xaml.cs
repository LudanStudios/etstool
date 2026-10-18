using ETSAssistant.WinUI3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class PreferencesPage : Page
{
    private readonly AppPreferencesService _preferences = AppPreferencesService.Current;
    private bool _loading;

    public PreferencesPage()
    {
        InitializeComponent();
        _loading = true;
        ThemeBox.SelectedIndex = _preferences.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };
        LanguageBox.SelectedIndex = _preferences.Language == "en-US" ? 1 : 0;
        _loading = false;
        LocalizationService.Current.Apply(this);
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _preferences.SetTheme(ThemeBox.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System"
        });
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _preferences.SetLanguage(LanguageBox.SelectedIndex == 1 ? "en-US" : "zh-CN");
    }
}
