using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using ETSAssistant.WinUI3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class ToolsPage : Page
{
    private static string T(string key) => LocalizationService.Current.Translate(key);
    private static string F(string key, params (string Name, object? Value)[] values) =>
        LocalizationService.Current.Format(key, values);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(1200) };
    private readonly AssistantService _service = AssistantService.Current;
    private readonly PacketCaptureService _capture = PacketCaptureService.Current;
    private readonly DeepSeekService _deepSeek = new(AssistantService.Current.AppRoot);
    private readonly SpeakingAnswerService _speaking = new(AssistantService.Current.DataRoot);
    private readonly AliyunTtsService _aliyunTts = new(AssistantService.Current.AppRoot);
    private readonly SpeakingSessionStore _sessionStore = new(AssistantService.Current.AppRoot);
    private readonly VirtualCableService _cable = new();
    private SpeakingSession? _session;
    private string? _speakingPackId;
    private IReadOnlyList<SpeakingPaper> _speakingPapers = Array.Empty<SpeakingPaper>();
    private string? _lastTtsWavPath;
    private readonly SpeakingAutoRunner _autoSpeakingRunner = new();
    private bool _portAvailable;
    private bool _updatingCapture;
    private bool _updatingVerbose;

    public ToolsPage()
    {
        InitializeComponent();
        _capture.StatusChanged += OnCaptureStatusChanged;
        _autoSpeakingRunner.Output += ReportAutoSpeaking;
        _autoSpeakingRunner.Exited += () => DispatcherQueue.TryEnqueue(() =>
        {
            AutoSpeakingButtonText.Text = T("common.start_automatic_speaking");
            AutoSpeakingStatusText.Text = T("speaking.auto_speaking_ended");
        });
        Unloaded += (_, _) =>
        {
            _capture.StatusChanged -= OnCaptureStatusChanged;
        };
        Loaded += async (_, _) =>
        {
            RefreshLogSummary();
            RefreshDiagnosticLogViewer();
            RefreshDeepSeekStatus();
            RefreshAliyunTtsStatus();
            RefreshAudioStatus();
            UpdateSpeakingModeUi();
            UpdateCaptureUi();
            _updatingVerbose = true;
            VerboseLoggingToggle.IsOn = AppPreferencesService.Current.VerboseLoggingEnabled;
            _updatingVerbose = false;
            EnvironmentText.Text = BuildEnvironmentText();
            await RefreshCableAsync();
            await ProbePortAsync();
        };
    }

    private void OnCaptureStatusChanged() => DispatcherQueue.TryEnqueue(UpdateCaptureUi);

    private void UpdateCaptureUi()
    {
        if (!_updatingCapture)
        {
            _updatingCapture = true;
            CaptureToggle.IsOn = _capture.IsEnabled;
            CaptureStatusText.Text = T(_capture.Status);
            CapturePathText.Text = F("tools.capture_file_path", ("path", _capture.CapturePath));
            _updatingCapture = false;
        }
    }

    private void CaptureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingCapture) return;
        if (CaptureToggle.IsOn) _capture.Enable();
        else _capture.Disable();
        UpdateCaptureUi();
    }

    private void VerboseLoggingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingVerbose) return;
        AppPreferencesService.Current.SetVerboseLogging(VerboseLoggingToggle.IsOn);
        ShowMessage(T("tools.verbose_logs_updated"), T(VerboseLoggingToggle.IsOn
            ? "tools.listener_verbose_on" : "tools.listener_verbose_off"), InfoBarSeverity.Informational);
    }

    private void OpenCapture_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(_capture.CapturePath))
            {
                ShowMessage(T("tools.capture_file_missing_title"), T("tools.capture_file_missing_detail"),
                    InfoBarSeverity.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo(_capture.CapturePath) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowMessage(T("tools.open_capture_failed"), ex.Message, InfoBarSeverity.Error); }
    }

    private async Task<bool> ProbePortAsync()
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync("http://127.0.0.1:10086/json/version");
            _portAvailable = response.IsSuccessStatusCode;
        }
        catch (Exception) { _portAvailable = false; }

        PortDot.Fill = new SolidColorBrush(_portAvailable
            ? Color.FromArgb(255, 34, 197, 94)
            : Color.FromArgb(255, 148, 163, 184));
        PortStatusText.Text = T(_portAvailable ? "tools.port_available" : "tools.port_not_connected");
        EnvironmentText.Text = BuildEnvironmentText();
        return _portAvailable;
    }

    private async void OpenList_Click(object sender, RoutedEventArgs e) => await ShowEndpointAsync("json/list");
    private async void OpenVersion_Click(object sender, RoutedEventArgs e) => await ShowEndpointAsync("json/version");

    private async Task ShowEndpointAsync(string path)
    {
        string url = $"http://127.0.0.1:10086/{path}";
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(url);
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                ShowMessage(T("tools.endpoint_error"), F("tools.endpoint_http_error", ("url", url), ("status", (int)response.StatusCode)), InfoBarSeverity.Warning);
                return;
            }

            var viewer = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    Text = body, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
                }
            };
            var dialog = new ContentDialog
            {
                Title = $"127.0.0.1:10086/{path}",
                Content = viewer,
                PrimaryButtonText = T("tools.copy_json"),
                CloseButtonText = T("common.close"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) == ContentDialogResult.Primary) CopyText(body);
        }
        catch (Exception)
        {
            ShowMessage(T("tools.ets_port_not_open_title"), T("tools.ets_port_not_open_detail"),
                InfoBarSeverity.Warning);
        }
    }

    private void RefreshAudioStatus()
    {
        var audio = new SpeakingAudioService(_service.DataRoot, _service.EtsExe);
        string state = audio.HasInput ? F("tools.wav_ready_path", ("path", audio.InputPath)) :
            F("tools.wav_not_ready_path", ("path", audio.InputPath));
        AudioStatusText.Text = state + " · " + T(_service.IsEtsRunning ? "tools.ets_running_suffix" : "tools.ets_not_running_suffix");
    }

    private async void PickSpeakingWav_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            picker.FileTypeFilter.Add(".wav");
            MainWindow window = MainWindow.Current ?? throw new InvalidOperationException(T("common.window_not_ready"));
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var audio = new SpeakingAudioService(_service.DataRoot, _service.EtsExe);
            string output = await audio.PrepareAsync(file.Path);
            RefreshAudioStatus();
            ShowMessage(T("tools.wav_prepared_title"), F("tools.wav_prepared_detail", ("path", output)), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(T("tools.wav_prepare_failed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void UseLastTtsWav_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastTtsWavPath) || !File.Exists(_lastTtsWavPath))
        {
            ShowMessage(T("tools.tts_wav_unavailable_title"), T("tools.tts_wav_unavailable_detail"),
                InfoBarSeverity.Warning);
            return;
        }
        try
        {
            var audio = new SpeakingAudioService(_service.DataRoot, _service.EtsExe);
            string output = await audio.PrepareAsync(_lastTtsWavPath);
            RefreshAudioStatus();
            ShowMessage(T("tools.tts_wav_prepared_title"), F("tools.wav_prepared_detail", ("path", output)), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(T("tools.wav_prepare_tts_failed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private void LaunchEtsWithFakeMic_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var audio = new SpeakingAudioService(_service.DataRoot, _service.EtsExe);
            if (!audio.HasInput)
            {
                ShowMessage(T("tools.wav_missing_title"), T("tools.wav_missing_detail"), InfoBarSeverity.Warning);
                return;
            }
            if (_service.IsEtsRunning)
            {
                ShowMessage(T("tools.ets_already_running_title"), T("tools.close_ets_before_fake_mic"),
                    InfoBarSeverity.Warning);
                return;
            }

            bool loop = LoopAudioCheck.IsChecked == true;
            _service.OpenEts(audio.BuildFakeMicrophoneArguments(loop));
            RefreshAudioStatus();
            ShowMessage(T("tools.ets_start_requested"), T("tools.ets_fake_mic_launch_detail"),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(T("tools.start_failed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task<bool> RefreshCableAsync()
    {
        try { await _cable.RefreshAsync(); return true; }
        catch (Exception ex)
        {
            DiagnosticLogService.Warn("developer_tools.virtual_cable.refresh_failed", ex.Message);
            return false;
        }
    }

    private void RefreshDeepSeekStatus()
    {
        string keyState = ApiKeyStore.HasKey
            ? T("tools.api_key_configured_direct")
            : T("tools.api_key_missing");
        string attachment = _deepSeek.FindAttachment() ?? T("tools.attachment_missing");
        string mode = T(AppPreferencesService.Current.SpeakingMode == "flexible" ? "tools.generated_mode" : "tools.standard_mode");
        DeepSeekStatusText.Text = F("tools.deepseek_status_summary", ("mode", mode), ("keyState", keyState),
            ("model", DeepSeekService.Model), ("attachment", attachment));
    }

    private void RefreshAliyunTtsStatus()
    {
        string status = string.Join(Environment.NewLine,
            AliyunTtsCredentialStore.ConfigurationStatus,
            AliyunTtsCredentialStore.CredentialSourceStatus);
        AliyunTtsStatusText.Text = status;
    }

    private void UpdateSpeakingModeUi()
    {
        bool flexible = AppPreferencesService.Current.SpeakingMode == "flexible";
        SpeakingModeSummaryText.Text = F("tools.speaking_mode_summary",
            ("mode", T(flexible ? "tools.generated_mode" : "tools.standard_mode")));
        AskDeepSeekText.Text = T(flexible ? "tools.send_to_deepseek" : "tools.extract_standard_answer");
    }

    private async void LoadSpeakingQuestions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<SpeakingPaper> papers = await Task.Run(() => _speaking.LoadAllPapers());
            _speakingPapers = papers;
            SpeakingPaperBox.ItemsSource = papers;
            if (papers.Count == 0)
            {
                ShowMessage(T("tools.no_speaking_questions"), T("tools.no_speaking_questions_found"),
                    InfoBarSeverity.Warning);
                return;
            }
            SpeakingPaperBox.SelectedIndex = 0;
            int total = papers.Sum(paper => paper.Questions.Count);
            ShowMessage(T("tools.speaking_questions_loaded"), F("tools.speaking_question_count", ("papers", papers.Count), ("questions", total)),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(T("tools.load_speaking_questions_failed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private void SpeakingPaperBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeakingPaperBox.SelectedItem is not SpeakingPaper paper) return;
        _speakingPackId = paper.PackId;
        SpeakingQuestionBox.ItemsSource = paper.Questions;
        if (paper.Questions.Count > 0) SpeakingQuestionBox.SelectedIndex = 0;
    }

    private void SpeakingQuestionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeakingQuestionBox.SelectedItem is not SpeakingQuestion question) return;
        SpeakingStemBox.Text = question.RequestText;
        if (AppPreferencesService.Current.SpeakingMode != "standard") return;
        if (question.StandardAnswer.Length == 0)
        {
            ShowMessage(T("tools.question_no_standard_answer"), T("tools.question_no_standard_answer_detail"),
                InfoBarSeverity.Warning);
            return;
        }
        SpeakingResultBox.Text = question.StandardAnswer;
    }

    private async Task<SpeakingQuestion?> ResolveSpeakingQuestionAsync(string stem)
    {
        if (SpeakingQuestionBox.SelectedItem is SpeakingQuestion selected &&
            string.Equals(selected.RequestText.Trim(), stem.Trim(), StringComparison.Ordinal))
            return selected;

        if (_speakingPackId is not null)
        {
            SpeakingQuestion? current = _speaking.Find(_speakingPackId, stem);
            if (current is not null) return current;
        }

        foreach (SpeakingPaper paper in _speakingPapers)
        {
            SpeakingQuestion? match = _speaking.Find(paper.PackId, stem);
            if (match is not null) return match;
        }

        _speakingPackId ??= await CurrentPaperService.FindActivePackIdAsync() ?? _speaking.FindRecentPackId();
        return _speakingPackId is null ? null : _speaking.Find(_speakingPackId, stem);
    }

    private async void AskDeepSeek_Click(object sender, RoutedEventArgs e)
    {
        string stem = SpeakingStemBox.Text.Trim();
        if (stem.Length == 0)
        {
            ShowMessage(T("tools.missing_prompt_title"), T("tools.enter_prompt_before_send"), InfoBarSeverity.Warning);
            return;
        }

        if (AppPreferencesService.Current.SpeakingMode != "flexible")
        {
            SpeakingQuestion? question = await ResolveSpeakingQuestionAsync(stem);
            if (question is null || question.StandardAnswer.Length == 0)
            {
                ShowMessage(T("tools.no_matching_answer"), T("tools.no_matching_answer_detail"),
                    InfoBarSeverity.Warning);
                return;
            }
            SpeakingResultBox.Text = question.StandardAnswer;
            ShowMessage(T("tools.standard_answer_extracted"),
                F("tools.standard_answer_source", ("paperId", _speakingPackId ?? T("common.current_paper")), ("count", question.StandardAnswer.Length)),
                InfoBarSeverity.Success);
            return;
        }

        if (!ApiKeyStore.HasKey)
        {
            if (MainWindow.Current is { } mainWindow)
                await mainWindow.PromptForMissingCredentialsAsync();
            return;
        }

        AskDeepSeekButton.IsEnabled = false;
        DeepSeekProgress.IsActive = true;
        try
        {
            DeepSeekResult result = await _deepSeek.AskSpeakingAsync(stem);
            if (result.Success)
            {
                SpeakingResultBox.Text = result.Text;
                ShowMessage(T("tools.deepseek_returned"), F("tools.deepseek_result_summary", ("model", DeepSeekService.Model), ("count", result.Text.Length)),
                    InfoBarSeverity.Success);
            }
            else
            {
                ShowMessage(T("tools.deepseek_call_failed"), TtsError(result.Error), InfoBarSeverity.Error);
            }
        }
        finally
        {
            DeepSeekProgress.IsActive = false;
            AskDeepSeekButton.IsEnabled = true;
            RefreshDeepSeekStatus();
        }
    }

    private void ClearSpeaking_Click(object sender, RoutedEventArgs e)
    {
        SpeakingStemBox.Text = string.Empty;
        SpeakingResultBox.Text = string.Empty;
    }

    private void CopySpeakingResult_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SpeakingResultBox.Text))
        {
            ShowMessage(T("tools.no_result"), T("tools.send_prompt_before_copy"), InfoBarSeverity.Warning);
            return;
        }
        CopyText(SpeakingResultBox.Text);
        ShowMessage(T("tools.result_copied"), T("tools.model_result_copied"), InfoBarSeverity.Success);
    }

    private async void AliyunTts_Click(object sender, RoutedEventArgs e)
    {
        string text = SpeakingResultBox.Text.Trim();
        if (text.Length == 0)
        {
            ShowMessage(T("tools.no_synthesizable_answer"), T("tools.generate_or_extract_before_tts"), InfoBarSeverity.Warning);
            return;
        }
        if (!AliyunTtsCredentialStore.HasCredentials)
        {
            if (MainWindow.Current is { } mainWindow)
                await mainWindow.PromptForMissingCredentialsAsync();
            return;
        }

        AliyunTtsButton.IsEnabled = false;
        AliyunTtsProgress.IsActive = true;
        try
        {
            AliyunTtsResult result = await _aliyunTts.SynthesizeToWavAsync(text);
            if (!result.Success)
            {
                ShowMessage(T("tools.aliyun_tts_failed"), TtsError(result.Error), InfoBarSeverity.Error);
                return;
            }

            AliyunTtsStatusText.Text = F("tools.wav_saved_path", ("path", result.FilePath));
            _lastTtsWavPath = result.FilePath;
            Process.Start(new ProcessStartInfo(result.FilePath) { UseShellExecute = true });
            ShowMessage(T("tools.aliyun_tts_complete"), F("tools.tts_opened_wav", ("count", result.ChunkCount)), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(T("tools.aliyun_tts_failed"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            AliyunTtsProgress.IsActive = false;
            AliyunTtsButton.IsEnabled = true;
            RefreshAliyunTtsStatusIfNoOutput();
        }
    }

    private void RefreshAliyunTtsStatusIfNoOutput()
    {
        if (string.IsNullOrWhiteSpace(AliyunTtsStatusText.Text))
            AliyunTtsStatusText.Text = string.Join(Environment.NewLine,
                AliyunTtsCredentialStore.ConfigurationStatus,
                AliyunTtsCredentialStore.CredentialSourceStatus);
    }

    private void CopyPort_Click(object sender, RoutedEventArgs e)
    {
        CopyText("http://127.0.0.1:10086/json/list");
        ShowMessage(T("tools.address_copied"), T("tools.debug_address_copied"), InfoBarSeverity.Success);
    }

    private async void RefreshPort_Click(object sender, RoutedEventArgs e)
    {
        bool available = await ProbePortAsync();
        ShowMessage(T(available ? "tools.port_available" : "tools.port_not_connected"),
            T(available ? "tools.port_available_message" : "tools.start_debug_ets"),
            available ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private async void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = $"ets-logs-{DateTime.Now:yyyyMMdd-HHmmss}",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeChoices.Add(T("tools.zip_file_type"), new List<string> { ".zip" });
            MainWindow window = MainWindow.Current ?? throw new InvalidOperationException(T("common.window_not_ready"));
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            int count = await Task.Run(() => WriteLogArchive(file.Path));
            RefreshLogSummary();
            ShowMessage(T("tools.logs_exported"), F("tools.logs_exported_count", ("count", count), ("path", file.Path)), InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowMessage(T("tools.export_failed"), ex.Message, InfoBarSeverity.Error); }
    }

    private int WriteLogArchive(string destination)
    {
        if (File.Exists(destination)) File.Delete(destination);
        using ZipArchive archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        int count = 0;
        if (Directory.Exists(_service.DataRoot))
        {
            foreach (string file in Directory.EnumerateFiles(_service.DataRoot, "*.log").OrderBy(Path.GetFileName))
            {
                AddFile(archive, file, $"logs/{Path.GetFileName(file)}");
                count++;
            }
        }

        foreach (string file in DiagnosticLogService.GetLogFiles().OrderBy(Path.GetFileName))
        {
            AddFile(archive, file, $"logs/diagnostics/{Path.GetFileName(file)}");
            count++;
        }

        string config = Path.Combine(_service.AppRoot, "ets_answer_config.yaml");
        if (File.Exists(config)) { AddFile(archive, config, "config/ets_answer_config.yaml"); count++; }
        string installation = Path.Combine(_service.DataRoot, "ets_install_dir.txt");
        if (File.Exists(installation)) { AddFile(archive, installation, "config/ets_install_dir.txt"); count++; }

        ZipArchiveEntry entry = archive.CreateEntry("environment.txt", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
            writer.Write(BuildEnvironmentText());
        return count + 1;
    }

    private static void AddFile(ZipArchive archive, string path, string entryName)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream target = entry.Open();
        source.CopyTo(target);
    }

    private void RefreshLogSummary()
    {
        var files = (Directory.Exists(_service.DataRoot)
                ? Directory.EnumerateFiles(_service.DataRoot, "*.log")
                : Enumerable.Empty<string>())
            .Select(path => new FileInfo(path))
            .OrderBy(info => info.Name)
            .ToList();
        var diagnosticFiles = DiagnosticLogService.GetLogFiles()
            .Select(path => new FileInfo(path))
            .OrderBy(info => info.Name)
            .ToList();
        string backgroundSummary = files.Count == 0
            ? T("tools.no_background_logs")
            : string.Join(" · ", files.Select(info => $"{info.Name} {info.Length / 1024.0:0.#} KB"));
        string diagnosticSummary = diagnosticFiles.Count == 0
            ? T("tools.no_diagnostic_logs")
            : string.Join(" · ", diagnosticFiles.Select(info => $"{info.Name} {info.Length / 1024.0:0.#} KB"));
        LogSummaryText.Text = F("tools.log_summary", ("diagnosticPath", DiagnosticLogService.LogDirectory),
            ("diagnosticSummary", diagnosticSummary), ("backgroundPath", _service.DataRoot),
            ("backgroundSummary", backgroundSummary));
    }

    private void DiagnosticLogFilter_Click(object sender, RoutedEventArgs e) => RefreshDiagnosticLogViewer();

    private void RefreshDiagnosticLogs_Click(object sender, RoutedEventArgs e) => RefreshDiagnosticLogViewer();

    private void RefreshDiagnosticLogViewer()
    {
        var selectedLevels = new HashSet<string>(StringComparer.Ordinal)
        {
            InfoLogFilter.IsChecked == true ? "INFO" : string.Empty,
            WarnLogFilter.IsChecked == true ? "WARN" : string.Empty,
            ErrorLogFilter.IsChecked == true ? "ERROR" : string.Empty,
            FatalLogFilter.IsChecked == true ? "FATAL" : string.Empty
        };
        selectedLevels.Remove(string.Empty);

        string[] recentLines = DiagnosticLogService.ReadRecentEntries(1200)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        string[] visibleLines = recentLines
            .Where(line => TryGetDiagnosticLogLevel(line) is string level && selectedLevels.Contains(level))
            .ToArray();

        LocalizationService localization = LocalizationService.Current;
        string text = visibleLines.Length == 0
            ? recentLines.Length == 0
                ? localization.Translate("tools.no_diagnostic_logs_yet")
                : localization.Translate("tools.no_logs_match_selected_levels")
            : string.Join(Environment.NewLine, visibleLines);
        if (!string.Equals(DiagnosticLogText.Text, text, StringComparison.Ordinal))
        {
            DiagnosticLogText.Text = text;
            DispatcherQueue.TryEnqueue(() => DiagnosticLogText.Select(DiagnosticLogText.Text.Length, 0));
        }
        DiagnosticLogViewerStatusText.Text = localization.Format(
            "tools.diagnostic_log_status",
            ("visible", visibleLines.Length),
            ("read", recentLines.Length),
            ("time", DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)));
    }

    private static string? TryGetDiagnosticLogLevel(string line)
    {
        if (!line.StartsWith('[')) return null;
        int closingBracket = line.IndexOf(']');
        return closingBracket > 1 ? line[1..closingBracket] : null;
    }

    private void CopyEnvironment_Click(object sender, RoutedEventArgs e)
    {
        CopyText(BuildEnvironmentText());
        ShowMessage(T("tools.environment_copied"), T("tools.environment_copied_detail"), InfoBarSeverity.Success);
    }

    private string BuildEnvironmentText()
    {
        var lines = new List<string>
        {
            F("tools.environment_time", ("time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))),
            F("tools.environment_app_folder", ("path", _service.AppRoot)),
            F("tools.environment_data_folder", ("path", _service.DataRoot)),
            F("tools.environment_diagnostic_logs", ("path", DiagnosticLogService.LogDirectory)),
            F("tools.environment_ets_program", ("path", _service.EtsExe ?? T("tools.ets_not_found"))),
            F("tools.environment_engine", ("path", _service.EnginePath)),
            F("tools.environment_debug_port", ("status", T(_portAvailable ? "tools.port_available" : "tools.port_not_connected"))),
            F("tools.environment_ets_process", ("status", T(_service.IsEtsRunning ? "tools.ets_running" : "tools.ets_not_running"))),
            AliyunTtsCredentialStore.ConfigurationStatus,
            AliyunTtsCredentialStore.CredentialSourceStatus,
            F("tools.environment_system", ("system", Environment.OSVersion.VersionString))
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(_service.DataRoot);

    private void OpenDiagnosticLogs_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(DiagnosticLogService.LogDirectory); }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("developer_tools.diagnostics_directory.create_failed", ex);
            ShowMessage(T("tools.open_folder_failed"), ex.Message, InfoBarSeverity.Error);
            return;
        }
        OpenFolder(DiagnosticLogService.LogDirectory);
    }
    private void OpenProgram_Click(object sender, RoutedEventArgs e) => OpenFolder(_service.AppRoot);

    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(F("tools.directory_not_found", ("path", path)));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowMessage(T("tools.open_folder_failed"), ex.Message, InfoBarSeverity.Error); }
    }

    private async void FullReset_Click(object sender, RoutedEventArgs e)
    {
        var content = new TextBlock
        {
            Text = T("tools.full_reset_warning"),
            TextWrapping = TextWrapping.Wrap
        };
        var dialog = new ContentDialog
        {
            Title = T("tools.full_reset_question"),
            Content = content,
            PrimaryButtonText = T("tools.confirm_reset"),
            CloseButtonText = T("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        try
        {
            var service = new FullResetService(_service);
            FullResetResult result = await service.RunAsync();
            RefreshLogSummary();
            RefreshDeepSeekStatus();
            RefreshAliyunTtsStatus();
            RefreshAudioStatus();
            EnvironmentText.Text = BuildEnvironmentText();
            ShowMessage(T("tools.reset_complete"),
                string.Join(Environment.NewLine, result.Steps.Concat(result.Notes)),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMessage(T("tools.reset_failed"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private void AutoSpeaking_Click(object sender, RoutedEventArgs e)
    {
        if (_autoSpeakingRunner.IsRunning)
        {
            _autoSpeakingRunner.Stop();
            AutoSpeakingStatusText.Text = T("tools.reset_stopping_auto_speaking");
            return;
        }
        StartAutoSpeaking();
    }

    private async void StartAutoSpeaking()
    {
        _session ??= _sessionStore.Load();
        if (_session is null || _session.Items.Count == 0)
        {
            ShowMessage(T("tools.no_speaking_session"), T("tools.prepare_tts_before_start"),
                InfoBarSeverity.Warning);
            return;
        }
        string[] missing = _session.Items
            .Where(item => !File.Exists(item.WavPath))
            .Select(item => item.WavName)
            .ToArray();
        if (missing.Length > 0)
        {
            ShowMessage(T("tools.audio_files_missing"), F("tools.audio_files_missing_detail", ("files", string.Join(Environment.NewLine, missing))),
                InfoBarSeverity.Warning);
            return;
        }
        if (!await RefreshCableAsync() || !_cable.IsReady)
        {
            ShowMessage(T("tools.virtual_cable_not_ready"),
                _cable.IsAvailable
                    ? T("speaking.cable_output_not_enabled")
                    : T("speaking.virtual_cable_not_installed"),
                InfoBarSeverity.Warning);
            return;
        }

        if (!_autoSpeakingRunner.Start(_service.AppRoot, _service.DataRoot, _sessionStore.FilePath, out string error))
        {
            ShowMessage(T("tools.auto_speaking_start_failed"), TtsError(error), InfoBarSeverity.Error);
            return;
        }
        AutoSpeakingButtonText.Text = T("tools.stop_automatic_speaking");
        AutoSpeakingStatusText.Text = T("tools.auto_speaking_running_status");
    }

    private void ReportAutoSpeaking(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        DispatcherQueue.TryEnqueue(() => AutoSpeakingStatusText.Text = line.Trim());
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity)
    {
        InfoBarPresenter.Show(MessageBar, title, message, severity);
    }

    private static string TtsError(string? message) =>
        LocalizationService.Current.Translate(message ?? string.Empty);
}
