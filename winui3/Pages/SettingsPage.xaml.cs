using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private bool _loading = true;
    private AssistantSettings? _savedSettings;
    private bool _virtualCableBusy;
    private bool _csvPickerActive;
    private static string T(string key) => LocalizationService.Current.Translate(key);
    private static string F(string key, params (string Name, object? Value)[] values) =>
        LocalizationService.Current.Format(key, values);

    public SettingsPage()
    {
        InitializeComponent();
        LoadValues();
        Loaded += (_, _) => { AppPreferencesService.Current.AutomationChanged += RefreshAutomation; RefreshAutomation(); };
        Unloaded += (_, _) => AppPreferencesService.Current.AutomationChanged -= RefreshAutomation;
    }

    private void RefreshAutomation()
    {
        bool previousLoading = _loading;
        _loading = true;
        var preferences = AppPreferencesService.Current;
        SelectionToggle.IsOn = preferences.AutomaticSelectionEnabled;
        SpeakingToggle.IsOn = preferences.AutomaticSpeakingEnabled;
        AccuracySlider.IsEnabled = preferences.AutomaticSelectionEnabled;
        SkipBox.IsEnabled = preferences.AutomaticSelectionEnabled;
        SpeakingModeBox.IsEnabled = preferences.AutomaticSpeakingEnabled;
        AccuracySettings.Opacity = SkipSettings.Opacity = preferences.AutomaticSelectionEnabled ? 1 : 0.5;
        SpeakingSettings.Opacity = preferences.AutomaticSpeakingEnabled ? 1 : 0.5;
        _loading = previousLoading;
    }

    private void SelectionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { AppPreferencesService.Current.SetAutomaticSelection(SelectionToggle.IsOn); }
        catch (Exception ex)
        {
            RefreshAutomation();
            InfoBarPresenter.Show(SaveInfoBar, T("overview.need_attention"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private void SpeakingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) AppPreferencesService.Current.SetAutomaticSpeaking(SpeakingToggle.IsOn);
    }

    private void CredentialsContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject content)
            LocalizationService.Current.Apply(content);
    }

    private void LoadValues()
    {
        _loading = true;
        var settings = _service.LoadSettings();
        _savedSettings = settings;
        AccuracySlider.Value = settings.Accuracy;
        AccuracyText.Text = $"{Math.Round(settings.Accuracy * 100):0}%";
        SkipBox.Value = settings.SkipInterval;
        AutostartCheck.IsChecked = settings.StartAtLogin;
        MinimizeToTrayCheck.IsChecked = AppPreferencesService.Current.MinimizeToTrayEnabled;
        EtsPathText.Text = _service.EtsExe ?? T("settings.ets_path_not_found");
        SpeakingModeBox.SelectedIndex = AppPreferencesService.Current.SpeakingMode == "flexible" ? 1 : 0;
        _loading = false;
        RefreshAutomation();
        UpdateApiKeyStatus();
        UpdateAliyunStatus();
        _ = RefreshVirtualCableStatusAsync();
    }

    public void ExpandCredentials() => CredentialsExpander.IsExpanded = true;

    private async Task RefreshVirtualCableStatusAsync()
    {
        try
        {
            var cable = new VirtualCableService();
            await cable.RefreshAsync();
            CableStatusText.Text = cable.IsReady
                ? F("settings.cable_ready", ("input", cable.Current!.Name), ("output", cable.Capture!.Name))
                : cable.IsAvailable
                    ? F("settings.cable_capture_missing", ("input", cable.Current!.Name))
                    : T("settings.cable_not_detected");
        }
        catch (Exception ex)
        {
            CableStatusText.Text = F("settings.cable_check_failed", ("detail", ex.Message));
        }
    }

    private async void RefreshVirtualCable_Click(object sender, RoutedEventArgs e) =>
        await RefreshVirtualCableStatusAsync();

    private async void InstallVirtualCable_Click(object sender, RoutedEventArgs e) =>
        await RunVirtualCableSetupAsync(uninstall: false);

    private async void UninstallVirtualCable_Click(object sender, RoutedEventArgs e) =>
        await RunVirtualCableSetupAsync(uninstall: true);

    private async Task RunVirtualCableSetupAsync(bool uninstall)
    {
        if (_virtualCableBusy) return;
        _virtualCableBusy = true;
        InstallVirtualCableButton.IsEnabled = false;
        UninstallVirtualCableButton.IsEnabled = false;
        RefreshVirtualCableButton.IsEnabled = false;

        try
        {
            if (!VirtualCableSetupService.HasInstaller(_service.AppRoot))
            {
                InfoBarPresenter.Show(CableInfoBar, T("settings.driver_package_not_found"),
                    F("settings.check_app_file", ("path", VirtualCableSetupService.PackagePath(_service.AppRoot))),
                    InfoBarSeverity.Error);
                return;
            }

            string action = T(uninstall ? "settings.action_uninstall" : "settings.action_install");
            var dialog = new ContentDialog
            {
                Title = T(uninstall ? "settings.uninstall_driver_title" : "settings.install_driver_title"),
                Content = T(uninstall ? "settings.uninstall_driver_instructions" : "settings.install_driver_instructions"),
                PrimaryButtonText = T(uninstall ? "settings.open_uninstaller" : "settings.open_installer"),
                CloseButtonText = T("settings.cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

            InfoBarPresenter.Show(CableInfoBar, F("settings.setup_running", ("action", action)),
                T("settings.setup_admin_instructions"),
                InfoBarSeverity.Informational);
            int exitCode = await VirtualCableSetupService.RunSetupAsync(_service.AppRoot);
            if (exitCode != 0)
            {
                InfoBarPresenter.Show(CableInfoBar, F("settings.setup_exited", ("action", action)),
                    F("settings.setup_exit_code", ("code", exitCode)), InfoBarSeverity.Warning);
                return;
            }

            await RefreshVirtualCableStatusAsync();
            if (uninstall)
            {
                await OfferCableRestartAsync();
            }
            else
            {
                InfoBarPresenter.Show(CableInfoBar, T("settings.setup_closed"),
                    T("settings.restart_after_driver_change"),
                    InfoBarSeverity.Success);
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            InfoBarPresenter.Show(CableInfoBar, T("settings.operation_cancelled"),
                T("settings.admin_permission_not_granted"), InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            InfoBarPresenter.Show(CableInfoBar, T("settings.driver_operation_failed"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _virtualCableBusy = false;
            InstallVirtualCableButton.IsEnabled = true;
            UninstallVirtualCableButton.IsEnabled = true;
            RefreshVirtualCableButton.IsEnabled = true;
        }
    }

    private async Task OfferCableRestartAsync()
    {
        var dialog = new ContentDialog
        {
            Title = T("settings.restart_question"),
            Content = T("settings.restart_driver_removal_warning"),
            PrimaryButtonText = T("settings.restart_now"),
            CloseButtonText = T("settings.restart_later"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            InfoBarPresenter.Show(CableInfoBar, T("settings.restart_failed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private void SpeakingModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SpeakingModeBox.SelectedItem is ComboBoxItem { Tag: string mode })
            AppPreferencesService.Current.SetSpeakingMode(mode);
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox checkBox) return;
        AppPreferencesService.Current.SetMinimizeToTray(checkBox.IsChecked == true);
    }

    private void UpdateApiKeyStatus() =>
        ApiKeyStatusText.Text = T(ApiKeyStore.HasKey ? "settings.status_saved_encrypted_locally" : "settings.status_not_configured");

    private void UpdateAliyunStatus() =>
        AliyunStatusText.Text = AliyunTtsCredentialStore.HasCredentials
            ? T("settings.status_aliyun_saved")
            : T("settings.status_aliyun_missing");

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        string key = ApiKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            InfoBarPresenter.Show(ApiKeyInfoBar, T("settings.api_key_required_title"),
                T("settings.api_key_required_message"), InfoBarSeverity.Warning);
            return;
        }

        try
        {
            ApiKeyStore.Save(key);
            ApiKeyBox.Password = string.Empty;
            UpdateApiKeyStatus();
            InfoBarPresenter.Show(ApiKeyInfoBar, T("settings.credential_saved_title"),
                T("settings.credential_saved_message"),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            InfoBarPresenter.Show(ApiKeyInfoBar, T("settings.credential_save_failed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ClearApiKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyStore.Clear();
        ApiKeyBox.Password = string.Empty;
        UpdateApiKeyStatus();
        InfoBarPresenter.Show(ApiKeyInfoBar, T("settings.credential_cleared_title"), T("settings.deepseek_key_deleted"),
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
            InfoBarPresenter.Show(AliyunInfoBar, T("settings.aliyun_credential_saved_title"),
                T("settings.credentials_saved_encrypted"),
                InfoBarSeverity.Success);
        }
        catch (Exception ex) { InfoBarPresenter.Show(AliyunInfoBar, T("settings.credential_save_failed"), ex.Message, InfoBarSeverity.Error); }
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
            InfoBarPresenter.Show(AliyunInfoBar, T("settings.aliyun_credential_cleared_title"), T("settings.aliyun_credentials_deleted"),
                InfoBarSeverity.Informational);
        }
        catch (Exception ex) { InfoBarPresenter.Show(AliyunInfoBar, T("settings.credentials_clear_failed"), ex.Message, InfoBarSeverity.Error); }
    }

    private async void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_csvPickerActive) return;
        _csvPickerActive = true;
        try
        {
            MainWindow window = MainWindow.Current ?? throw new InvalidOperationException(T("settings.window_not_ready"));
            DiagnosticLogService.Info("settings.credentials.csv_picker.opened");
            string? csvPath = NativeCsvFilePicker.PickCsv(WindowNative.GetWindowHandle(window));
            if (csvPath is null)
            {
                DiagnosticLogService.Info("settings.credentials.csv_picker.cancelled");
                return;
            }
            string csvName = Path.GetFileName(csvPath);
            DiagnosticLogService.Info("settings.credentials.csv_picker.selected", $"file={csvName}");

            CredentialCsvPreview preview = CredentialCsvImporter.Parse(csvPath);
            if (!preview.HasAny)
            {
                string errors = string.Join(Environment.NewLine, preview.Errors.Take(5));
                CsvImportStatusText.Text = T("settings.no_credentials_imported");
                InfoBarPresenter.Show(CsvImportInfoBar, T("settings.csv_no_valid_credentials"), errors,
                    InfoBarSeverity.Error);
                return;
            }

            var summary = new StackPanel { Spacing = 6 };
            summary.Children.Add(new TextBlock { Text = T("settings.csv_import_overwrite_warning"), TextWrapping = TextWrapping.Wrap });
            if (preview.HasDeepSeek)
                summary.Children.Add(new TextBlock { Text = T("settings.csv_deepseek_item") });
            if (preview.HasAliyun)
                summary.Children.Add(new TextBlock { Text = T("settings.csv_aliyun_item") });
            summary.Children.Add(new TextBlock
            {
                Text = T("settings.csv_encryption_note"),
                FontSize = 12,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap
            });
            if (preview.Errors.Count > 0)
            {
                summary.Children.Add(new TextBlock
                {
                    Text = F("settings.csv_skipped_rows", ("count", preview.Errors.Count)) + Environment.NewLine +
                           string.Join(Environment.NewLine, preview.Errors.Take(3)),
                    FontSize = 12,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(0xFF, 252, 211, 77)),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            var dialog = new ContentDialog
            {
                Title = T("settings.csv_import_title"),
                Content = summary,
                PrimaryButtonText = T("settings.csv_import_encrypt"),
                CloseButtonText = T("common.cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

            CredentialImportResult result = CredentialCsvImporter.Apply(preview);
            ApiKeyBox.Password = string.Empty;
            UpdateApiKeyStatus();
            UpdateAliyunStatus();

            var imported = new List<string>();
            if (result.DeepSeekSaved) imported.Add("DeepSeek");
            if (result.AliyunSaved) imported.Add(T("settings.csv_aliyun_service"));
            CsvImportStatusText.Text = imported.Count > 0
                ? F("settings.csv_imported_from", ("items", string.Join(", ", imported)), ("file", csvName))
                : T("settings.csv_credentials_none_saved");

            InfoBarSeverity severity = !result.SavedAny
                ? InfoBarSeverity.Error
                : result.Errors.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            string message = result.Errors.Count > 0
                ? string.Join(Environment.NewLine, result.Errors.Take(5))
                : T("settings.csv_saved_no_export");
            InfoBarPresenter.Show(CsvImportInfoBar,
                T(result.SavedAny ? "settings.csv_import_complete" : "settings.csv_import_failed"), message, severity);
        }
        catch (Exception ex)
        {
            string detail = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            CsvImportStatusText.Text = F("settings.csv_import_error_detail", ("detail", detail));
            DiagnosticLogService.Error("settings.credentials.csv_import.failed", ex);
            InfoBarPresenter.Show(CsvImportInfoBar, T("settings.csv_failed"), detail, InfoBarSeverity.Error);
        }
        finally
        {
            _csvPickerActive = false;
        }
    }

    private void AccuracySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (AccuracyText is not null) AccuracyText.Text = $"{Math.Round(e.NewValue * 100):0}%";
        SaveAnswerSettings();
    }

    private void SkipBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => SaveAnswerSettings();

    private void Autostart_Click(object sender, RoutedEventArgs e) => SaveAnswerSettings(updateAutostart: true);

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        _service.RefreshEts();
        EtsPathText.Text = _service.EtsExe ?? T("settings.ets_path_not_found");
    }

    private async void ChooseEtsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder,
                CommitButtonText = T("settings.choose_this_folder") };
            picker.FileTypeFilter.Add("*");
            MainWindow window = MainWindow.Current ?? throw new InvalidOperationException(T("settings.window_not_ready"));
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            EtsDirectoryValidation validation = _service.ValidateEtsDirectory(folder.Path);
            if (!validation.IsValid)
            {
                InfoBarPresenter.Show(SaveInfoBar, T("settings.directory_validation_failed"), validation.Message, InfoBarSeverity.Error);
                return;
            }
            var dialog = new ContentDialog { Title = T("settings.apply_ets_directory_question"),
                Content = validation.ExecutablePath, PrimaryButtonText = T("settings.apply"), CloseButtonText = T("common.cancel"),
                DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
            if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
            string warnings = _service.ApplyEtsDirectory(validation.DirectoryPath);
            EtsPathText.Text = _service.EtsExe ?? T("settings.ets_path_not_found");
            InfoBarPresenter.Show(SaveInfoBar, T(warnings.Length == 0 ? "settings.ets_directory_updated" : "settings.directory_updated_warning"),
                warnings.Length == 0 ? validation.ExecutablePath ?? validation.DirectoryPath : warnings,
                warnings.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex) { InfoBarPresenter.Show(SaveInfoBar, T("settings.select_folder_failed"), ex.Message, InfoBarSeverity.Error); }
    }

    private void SaveAnswerSettings(bool updateAutostart = false)
    {
        if (_loading || _savedSettings is null) return;
        // An unfinished NumberBox edit is not a valid setting. Keep the last saved value.
        if (!double.IsFinite(SkipBox.Value) || SkipBox.Value < 0 || SkipBox.Value > 60) return;
        try
        {
            var settings = _savedSettings with
            {
                Accuracy = AccuracySlider.Value,
                SkipInterval = SkipBox.Value,
                StartAtLogin = AutostartCheck.IsChecked == true,
                VerboseLoggingEnabled = AppPreferencesService.Current.VerboseLoggingEnabled
            };
            if (settings == _savedSettings && !updateAutostart) return;
            _service.SaveSettings(settings);
            _savedSettings = settings with { Seed = _service.LoadSettings().Seed };
            if (updateAutostart)
            {
                string warning = _service.TryInstallAutostart(settings.StartAtLogin);
                if (warning.Length > 0)
                {
                    AutostartCheck.IsChecked = _service.IsAutostartEnabled;
                    _savedSettings = _savedSettings with { StartAtLogin = AutostartCheck.IsChecked == true };
                    InfoBarPresenter.Show(SaveInfoBar, T("settings.saved_autostart_warning"), warning, InfoBarSeverity.Warning);
                    return;
                }
                if (settings.StartAtLogin) _service.EnsureListener();
            }
            SaveInfoBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("settings.auto_save.failed", ex);
            InfoBarPresenter.Show(SaveInfoBar, T("settings.settings_save_failed"), ex.Message, InfoBarSeverity.Error);
        }
    }

}

/// <summary>
/// Uses the desktop common file dialog because this app runs elevated. The WinRT
/// FileOpenPicker broker cannot reliably activate with an elevated owner window.
/// </summary>
internal static class NativeCsvFilePicker
{
    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnExplorer = 0x00080000;
    private const int OfnHideReadOnly = 0x00000004;
    private const int FileNameBufferSize = 32768;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public nint Owner;
        public nint Instance;
        public nint Filter;
        public nint CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public nint File;
        public int MaxFile;
        public nint FileTitle;
        public int MaxFileTitle;
        public nint InitialDirectory;
        public nint Title;
        public int Flags;
        public ushort FileOffset;
        public ushort FileExtension;
        public nint DefaultExtension;
        public nint CustomData;
        public nint Hook;
        public nint TemplateName;
        public nint Reserved;
        public int ReservedValue;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName fileName);

    [DllImport("comdlg32.dll", EntryPoint = "CommDlgExtendedError")]
    private static extern uint CommDlgExtendedError();

    public static string? PickCsv(nint owner)
    {
        LocalizationService localization = LocalizationService.Current;
        if (owner == 0) throw new InvalidOperationException(localization.Translate("settings.window_handle_unavailable"));

        nint filter = Marshal.StringToCoTaskMemUni(
            $"{localization.Translate("settings.csv_file_type")}\0*.csv\0{localization.Translate("settings.all_file_type")}\0*.*\0\0");
        nint title = Marshal.StringToCoTaskMemUni(localization.Translate("settings.choose_csv_file"));
        nint extension = Marshal.StringToCoTaskMemUni("csv");
        char[] fileBuffer = new char[FileNameBufferSize];
        GCHandle fileBufferHandle = GCHandle.Alloc(fileBuffer, GCHandleType.Pinned);
        try
        {
            var dialog = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Owner = owner,
                Filter = filter,
                FilterIndex = 1,
                File = fileBufferHandle.AddrOfPinnedObject(),
                MaxFile = FileNameBufferSize,
                Title = title,
                DefaultExtension = extension,
                Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnHideReadOnly
            };

            if (GetOpenFileName(ref dialog))
            {
                int terminator = Array.IndexOf(fileBuffer, '\0');
                return new string(fileBuffer, 0, terminator < 0 ? fileBuffer.Length : terminator);
            }

            uint error = CommDlgExtendedError();
            if (error == 0) return null; // User cancelled.
            throw new Win32Exception(unchecked((int)error), localization.Format("settings.csv_picker_failed",
                ("code", error.ToString("X8"))));
        }
        finally
        {
            if (fileBufferHandle.IsAllocated) fileBufferHandle.Free();
            Marshal.FreeCoTaskMem(filter);
            Marshal.FreeCoTaskMem(title);
            Marshal.FreeCoTaskMem(extension);
        }
    }
}
