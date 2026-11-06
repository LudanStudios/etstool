using ETSAssistant.WinUI3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

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
        IReadOnlyList<LocalizationLanguage> languages = LocalizationService.GetAvailableLanguages();
        foreach (LocalizationLanguage language in languages)
            LanguageBox.Items.Add(new ComboBoxItem { Content = language.DisplayName, Tag = language.Code });

        int selectedLanguageIndex = 0;
        for (int index = 0; index < LanguageBox.Items.Count; index++)
        {
            if (LanguageBox.Items[index] is ComboBoxItem { Tag: string code } &&
                code.Equals(_preferences.Language, StringComparison.OrdinalIgnoreCase))
            {
                selectedLanguageIndex = index;
                break;
            }
        }
        if (LanguageBox.Items.Count > 0) LanguageBox.SelectedIndex = selectedLanguageIndex;
        AutoUpdateToggle.IsOn = _preferences.AutoUpdateEnabled;
        _loading = false;
        LocalizationService.Current.Apply(this);
        RefreshUpdateSettings();
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
        if (LanguageBox.SelectedItem is ComboBoxItem { Tag: string languageCode })
            _preferences.SetLanguage(languageCode);
    }

    private void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading && !_preferences.InternalTestNoUpdatesEnabled)
            _preferences.SetAutoUpdate(AutoUpdateToggle.IsOn);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_preferences.InternalTestNoUpdatesEnabled) return;
        CheckUpdatesButtonEnabled(false);
        UpdateStatusText.Text = LocalizationService.Current.Translate("preferences.checking_versions");
        try
        {
            UpdateCheckResult result = await new GitHubUpdateService().CheckAsync();
            _preferences.MarkUpdateCheck(result.CheckedAt);
            UpdateStatusText.Text = $"{result.Message} " + LocalizationService.Current.Format("preferences.update_status_checked",
                ("time", result.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
            if (result.UpdateAvailable)
            {
                var dialog = new ContentDialog
                {
                    Title = LocalizationService.Current.Translate("preferences.available_update"),
                    Content = result.Message + "\n\n" + LocalizationService.Current.Translate("preferences.open_github_question"),
                    PrimaryButtonText = LocalizationService.Current.Translate("preferences.open_github"),
                    CloseButtonText = LocalizationService.Current.Translate("common.later"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) == ContentDialogResult.Primary)
                    Process.Start(new ProcessStartInfo(result.Url) { UseShellExecute = true });
                InfoBarPresenter.Show(UpdateInfoBar, LocalizationService.Current.Translate("preferences.found_update"), result.Message, InfoBarSeverity.Informational);
            }
            else InfoBarPresenter.Show(UpdateInfoBar, LocalizationService.Current.Translate("preferences.check_completed"), result.Message, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = LocalizationService.Current.Format("preferences.check_failed", ("detail", ex.Message));
            InfoBarPresenter.Show(UpdateInfoBar, LocalizationService.Current.Translate("preferences.cannot_check_updates"), ex.Message, InfoBarSeverity.Warning);
        }
        finally { CheckUpdatesButtonEnabled(true); }
    }

    private void CheckUpdatesButtonEnabled(bool enabled)
    {
        CheckUpdatesButton.IsEnabled = enabled;
    }

    private void RefreshUpdateSettings()
    {
        bool internalTestMode = _preferences.InternalTestNoUpdatesEnabled;
        UpdateControlsPanel.Visibility = internalTestMode ? Visibility.Collapsed : Visibility.Visible;
        InternalTestNoUpdatesText.Visibility = internalTestMode ? Visibility.Visible : Visibility.Collapsed;
        if (!internalTestMode) return;

        Version? version = typeof(PreferencesPage).Assembly.GetName().Version;
        string versionText = version is null ? "1.3.1" : $"{version.Major}.{version.Minor}.{version.Build}";
        string format = LocalizationService.Current.Translate("preferences.current_version_internal_test_build");
        InternalTestNoUpdatesText.Text = string.Format(format, versionText);
    }
}
