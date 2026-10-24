using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage.Pickers;
using WinRT.Interop;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly AssistantService _service = AssistantService.Current;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        LoadValues();
    }

    private void LoadValues()
    {
        var settings = _service.LoadSettings();
        AccuracySlider.Value = settings.Accuracy;
        AccuracyText.Text = $"{Math.Round(settings.Accuracy * 100):0}%";
        SkipBox.Value = settings.SkipInterval;
        AutostartCheck.IsChecked = settings.StartAtLogin;
        AutoListeningCheck.IsChecked = AppPreferencesService.Current.AutoListeningEnabled;
        EtsPathText.Text = _service.EtsExe ?? "未找到 ETS 安装目录。";
        _loading = true;
        SpeakingModeBox.SelectedIndex = AppPreferencesService.Current.SpeakingMode == "flexible" ? 1 : 0;
        _loading = false;
        UpdateApiKeyStatus();
        UpdateAliyunStatus();
        _ = AppPreferencesService.Current.EnforceExperienceModeCredentialPolicy();
    }

    public void ExpandCredentials() => CredentialsExpander.IsExpanded = true;

    private void SpeakingModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SpeakingModeBox.SelectedItem is ComboBoxItem { Tag: string mode })
            AppPreferencesService.Current.SetSpeakingMode(mode);
    }

    private void UpdateApiKeyStatus() =>
        ApiKeyStatusText.Text = ApiKeyStore.HasKey ? "状态：已保存（本机加密）" : "状态：未配置";

    private void UpdateAliyunStatus() =>
        AliyunStatusText.Text = AliyunTtsCredentialStore.HasCredentials
            ? "状态：RAM 凭据和 TTS AppID 已保存在本机加密文件中。"
            : "状态：未配置；使用公用 TTS 需要启用体验模式。";

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        string key = ApiKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            InfoBarPresenter.Show(ApiKeyInfoBar, "尚未填写 API Key",
                "请先粘贴 DeepSeek API Key，再点击保存。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            ApiKeyStore.Save(key);
            ApiKeyBox.Password = string.Empty;
            UpdateApiKeyStatus();
            bool allCredentialsConfigured = AppPreferencesService.Current.EnforceExperienceModeCredentialPolicy();
            InfoBarPresenter.Show(ApiKeyInfoBar, "密钥已保存",
                "已使用 Windows DPAPI 加密保存到本机，测试工具页可以直接测试口语题。" +
                (allCredentialsConfigured ? " DeepSeek 和阿里云 TTS 凭据已齐全，体验模式已自动关闭。" : ""),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            InfoBarPresenter.Show(ApiKeyInfoBar, "保存失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ClearApiKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyStore.Clear();
        ApiKeyBox.Password = string.Empty;
        UpdateApiKeyStatus();
        _ = AppPreferencesService.Current.EnforceExperienceModeCredentialPolicy();
        InfoBarPresenter.Show(ApiKeyInfoBar, "密钥已清除", "本机保存的 DeepSeek API Key 已删除。",
            InfoBarSeverity.Informational);
    }

    private void SaveAliyun_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AliyunTtsCredentialStore.Save(new AliyunTtsCredentials(
                AliyunRamNameBox.Text.Trim(), AliyunRamKeyBox.Password.Trim(), AliyunAppIdBox.Password.Trim()));
            AliyunRamNameBox.Text = string.Empty;
            AliyunRamKeyBox.Password = string.Empty;
            AliyunAppIdBox.Password = string.Empty;
            UpdateAliyunStatus();
            bool allCredentialsConfigured = AppPreferencesService.Current.EnforceExperienceModeCredentialPolicy();
            InfoBarPresenter.Show(AliyunInfoBar, "阿里云凭据已保存",
                "凭据已使用 Windows DPAPI 加密保存在本机。" +
                (allCredentialsConfigured ? " DeepSeek 和阿里云 TTS 凭据已齐全，体验模式已自动关闭。" : ""),
                InfoBarSeverity.Success);
        }
        catch (Exception ex) { InfoBarPresenter.Show(AliyunInfoBar, "保存失败", ex.Message, InfoBarSeverity.Error); }
    }

    private void ClearAliyun_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AliyunTtsCredentialStore.Clear();
            AliyunRamNameBox.Text = string.Empty;
            AliyunRamKeyBox.Password = string.Empty;
            AliyunAppIdBox.Password = string.Empty;
            UpdateAliyunStatus();
            _ = AppPreferencesService.Current.EnforceExperienceModeCredentialPolicy();
            InfoBarPresenter.Show(AliyunInfoBar, "阿里云凭据已清除", "本机保存的阿里云 TTS 凭据已删除。",
                InfoBarSeverity.Informational);
        }
        catch (Exception ex) { InfoBarPresenter.Show(AliyunInfoBar, "清除失败", ex.Message, InfoBarSeverity.Error); }
    }

    private async void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".csv");
            MainWindow window = MainWindow.Current ?? throw new InvalidOperationException("窗口尚未准备好。");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            CredentialCsvPreview preview = CredentialCsvImporter.Parse(file.Path);
            if (!preview.HasAny)
            {
                string errors = string.Join(Environment.NewLine, preview.Errors.Take(5));
                CsvImportStatusText.Text = "未导入任何凭据。";
                InfoBarPresenter.Show(CsvImportInfoBar, "CSV 中没有有效凭据", errors,
                    InfoBarSeverity.Error);
                return;
            }

            var summary = new StackPanel { Spacing = 6 };
            summary.Children.Add(new TextBlock { Text = "将导入并覆盖同类型凭据：", TextWrapping = TextWrapping.Wrap });
            if (preview.HasDeepSeek)
                summary.Children.Add(new TextBlock { Text = "• DeepSeek API Key：1 项" });
            if (preview.HasAliyun)
                summary.Children.Add(new TextBlock { Text = "• 阿里云 TTS：AccessKey ID / Secret / AppKey 各 1 项" });
            summary.Children.Add(new TextBlock
            {
                Text = "导入后使用 Windows DPAPI 加密保存，不提供导出。",
                FontSize = 12,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap
            });
            if (preview.Errors.Count > 0)
            {
                summary.Children.Add(new TextBlock
                {
                    Text = "CSV 中有 " + preview.Errors.Count + " 行被跳过：" + Environment.NewLine +
                           string.Join(Environment.NewLine, preview.Errors.Take(3)),
                    FontSize = 12,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(0xFF, 252, 211, 77)),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            var dialog = new ContentDialog
            {
                Title = "导入 CSV 凭据",
                Content = summary,
                PrimaryButtonText = "导入并加密",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            CredentialImportResult result = CredentialCsvImporter.Apply(preview);
            ApiKeyBox.Password = string.Empty;
            UpdateApiKeyStatus();
            UpdateAliyunStatus();
            bool allCredentialsConfigured = AppPreferencesService.Current.EnforceExperienceModeCredentialPolicy();

            var imported = new List<string>();
            if (result.DeepSeekSaved) imported.Add("DeepSeek");
            if (result.AliyunSaved) imported.Add("阿里云 TTS");
            CsvImportStatusText.Text = imported.Count > 0
                ? $"已导入并加密：{string.Join("、", imported)}（来源 {file.Name}）"
                : "没有保存任何凭据。";

            InfoBarSeverity severity = !result.SavedAny
                ? InfoBarSeverity.Error
                : result.Errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            string message = result.Errors.Count > 0
                ? string.Join(Environment.NewLine, result.Errors.Take(5))
                : "凭据已使用 Windows DPAPI 加密保存。程序不提供导出。";
            if (allCredentialsConfigured)
                message += " DeepSeek 和阿里云 TTS 凭据已齐全，体验模式已自动关闭。";
            InfoBarPresenter.Show(CsvImportInfoBar,
                result.SavedAny ? "凭据导入完成" : "凭据导入失败", message, severity);
        }
        catch (Exception ex)
        {
            CsvImportStatusText.Text = "导入失败。";
            InfoBarPresenter.Show(CsvImportInfoBar, "CSV 导入失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void AccuracySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (AccuracyText is not null) AccuracyText.Text = $"{Math.Round(e.NewValue * 100):0}%";
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        _service.RefreshEts();
        EtsPathText.Text = _service.EtsExe ?? "未找到 ETS 安装目录。";
    }

    private async void ChooseEtsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder,
                CommitButtonText = "选择此文件夹" };
            picker.FileTypeFilter.Add("*");
            MainWindow window = MainWindow.Current ?? throw new InvalidOperationException("窗口尚未准备好。");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            EtsDirectoryValidation validation = _service.ValidateEtsDirectory(folder.Path);
            if (!validation.IsValid)
            {
                InfoBarPresenter.Show(SaveInfoBar, "目录校验失败", validation.Message, InfoBarSeverity.Error);
                return;
            }
            var dialog = new ContentDialog { Title = "应用 ETS 安装目录？",
                Content = validation.ExecutablePath, PrimaryButtonText = "应用", CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            string warnings = _service.ApplyEtsDirectory(validation.DirectoryPath);
            EtsPathText.Text = _service.EtsExe ?? "未找到 ETS 安装目录。";
            InfoBarPresenter.Show(SaveInfoBar, warnings.Length == 0 ? "ETS 目录已更新" : "目录已更新，但有提醒",
                warnings.Length == 0 ? validation.ExecutablePath ?? validation.DirectoryPath : warnings,
                warnings.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex) { InfoBarPresenter.Show(SaveInfoBar, "选择失败", ex.Message, InfoBarSeverity.Error); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!double.IsFinite(SkipBox.Value) || SkipBox.Value < 0 || SkipBox.Value > 60)
                throw new ArgumentOutOfRangeException(nameof(SkipBox), "跳题间隔必须在 0 到 60 秒之间。");
            AppPreferencesService.Current.SetAutoListening(AutoListeningCheck.IsChecked == true);
            var settings = new AssistantSettings(AccuracySlider.Value,
                Guid.NewGuid().ToString("N"), SkipBox.Value,
                AutostartCheck.IsChecked == true,
                AppPreferencesService.Current.AutoListeningEnabled,
                AppPreferencesService.Current.ExperienceModeEnabled);
            _service.SaveSettings(settings);
            string warning = _service.TryInstallAutostart(settings.StartAtLogin);
            if (settings.StartAtLogin) _service.EnsureListener();
            InfoBarPresenter.Show(SaveInfoBar, warning.Length == 0 ? "已保存" : "设置已保存，登录启动项未更新",
                warning.Length == 0 ? "配置和 Windows 登录启动项已更新。" : warning,
                warning.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            InfoBarPresenter.Show(SaveInfoBar, "保存失败", ex.Message, InfoBarSeverity.Error);
        }
    }
}
