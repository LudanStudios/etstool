using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3;

public sealed class SpeakingPrepItem : INotifyPropertyChanged
{
    private string _status = "speaking.status_waiting";
    private string _statusGlyph = "\uE823";
    private Brush _statusBrush = ResolveBrush("TextFillColorTertiaryBrush");
    private readonly string _kind;

    public SpeakingPrepItem(int number, string kind, string prompt)
    {
        Number = number.ToString();
        _kind = kind;
        Prompt = prompt;
    }

    public string Number { get; }
    public string Kind => LocalizationService.Current.Translate(_kind);
    public string Prompt { get; }

    public string Status
    {
        get => LocalizationService.Current.Translate(_status);
        set
        {
            if (_status == value) return;
            _status = value;
            (_statusGlyph, _statusBrush) = value switch
            {
                "speaking.status_completed" => ("\uE73E", ResolveBrush("SystemFillColorSuccessBrush")),
                "speaking.status_generating" => ("\uE895", ResolveBrush("AccentTextFillColorPrimaryBrush")),
                "speaking.status_failed" => ("\uEA39", ResolveBrush("SystemFillColorCriticalBrush")),
                _ => ("\uE823", ResolveBrush("TextFillColorTertiaryBrush"))
            };
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusGlyph)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
        }
    }

    public string StatusGlyph => _statusGlyph;
    public Brush StatusBrush => _statusBrush;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal static Brush ResolveBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? value))
        {
            if (value is Brush brush) return brush;
            if (value is Color color) return new SolidColorBrush(color);
        }
        return new SolidColorBrush(Colors.Gray);
    }
}

public sealed partial class SpeakingPrepWindow : Window
{
    public static SpeakingPrepWindow? ActiveWindow { get; private set; }

    private readonly ObservableCollection<SpeakingPrepItem> _items = new();
    private readonly SpeakingAutoRunner _autoRunner = new();
    private string _packId = string.Empty;
    private string _paperTitle = string.Empty;
    private int _questionCount;
    private CancellationTokenSource? _cancellation;
    private bool _sessionReady;
    private bool _autoStartInProgress;
    private bool _windowOpenedLogged;
    private int _resumeStartIndex;
    private string? _autoRunnerFailureStatus;

    public SpeakingPrepWindow()
    {
        InitializeComponent();
        DiagnosticLogService.TrackWindowButtons(RootGrid, "speaking_prep.window");
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            if (_windowOpenedLogged) return;
            _windowOpenedLogged = true;
            DiagnosticLogService.Info("speaking_prep.window.opened");
        };
        ActiveWindow = this;
        Title = LocalizationService.Current.Translate("speaking.default_title");
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        QuestionList.ItemsSource = _items;
        ApplyTheme();
        ConfigureWindow();
        AppPreferencesService.Current.LanguageChanged += ApplyLocalization;
        RootGrid.Loaded += (_, _) =>
        {
            // XAML text keys are attached to the visual tree only after the first layout.
            // Applying localization in the constructor can run before these descendants exist.
            LocalizationService.Current.ReloadIfChanged();
            ApplyLocalization();
        };
        AppPreferencesService.Current.ThemeChanged += ApplyTheme;
        _autoRunner.Output += line => DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = line;
            _autoRunnerFailureStatus = line;
        });
        _autoRunner.Exited += () => DispatcherQueue.TryEnqueue(() =>
        {
            StartAutoButtonText.Text = LocalizationService.Current.Translate("common.start_automatic_speaking");
            if (_autoRunner.LastExitCode == 10)
            {
                _ = RepairTimingAsync();
                return;
            }
            if (_autoRunner.LastExitCode is > 0 && !string.IsNullOrWhiteSpace(_autoRunnerFailureStatus))
            {
                StatusText.Text = _autoRunnerFailureStatus;
                ErrorBar.Severity = InfoBarSeverity.Error;
                ErrorBar.Message = _autoRunnerFailureStatus;
                ErrorBar.IsOpen = true;
                return;
            }
            StatusText.Text = LocalizationService.Current.Translate("speaking.auto_speaking_ended");
        });
        Closed += (_, _) =>
        {
            DiagnosticLogService.Info("speaking_prep.window.closed", $"pack={_packId}");
            AppPreferencesService.Current.ThemeChanged -= ApplyTheme;
            AppPreferencesService.Current.LanguageChanged -= ApplyLocalization;
            _autoRunner.Stop();
            _cancellation?.Cancel();
            if (ActiveWindow == this) ActiveWindow = null;
        };
    }

    public void Start(string packId, string paperTitle, IReadOnlyList<SpeakingQuestion> questions)
    {
        DiagnosticLogService.Info("speaking_prep.started", $"pack={packId} questionCount={questions.Count}");
        _packId = packId;
        _paperTitle = paperTitle;
        _questionCount = questions.Count;
        Title = LocalizationService.Current.Format("dynamic.speaking_prep_title", ("paperTitle", paperTitle));
        PackText.Text = LocalizationService.Current.Format("dynamic.speaking_prep_summary",
            ("paperId", packId), ("count", questions.Count));
        _items.Clear();
        foreach (SpeakingQuestion question in questions)
            _items.Add(new SpeakingPrepItem(question.Number, question.Type, Trim(question.Prompt)));

        OverallProgress.Minimum = 0;
        OverallProgress.Maximum = Math.Max(1, questions.Count);
        OverallProgress.Value = 0;
        ProgressCountText.Text = $"0/{questions.Count}";
        StatusText.Text = LocalizationService.Current.Translate("speaking.tts_generating");
        ErrorBar.IsOpen = false;
        _sessionReady = false;
        StartAutoButton.Visibility = Visibility.Collapsed;
        HeaderStatusText.Visibility = Visibility.Visible;
        StartAutoButtonText.Text = LocalizationService.Current.Translate("common.start_automatic_speaking");
        SetHeaderStatus("speaking.status_generating", "AccentTextFillColorPrimaryBrush");
        _ = PrepareAsync();
    }

    private async Task PrepareAsync()
    {
        _resumeStartIndex = 0;
        DiagnosticLogService.Info("speaking_prep.tts_generation.started", $"pack={_packId} questionCount={_items.Count}");
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<SpeakingPrepProgress>(update =>
        {
            OverallProgress.Maximum = Math.Max(1, update.Total);
            OverallProgress.Value = Math.Min(update.Completed, update.Total);
            ProgressCountText.Text = $"{Math.Min(update.Completed, update.Total)}/{update.Total}";
            StatusText.Text = LocalizationService.Current.Translate(update.Message);
            for (int index = 0; index < _items.Count; index++)
            {
                _items[index].Status = index < update.Completed
                    ? "speaking.status_completed"
                    : index == update.Completed ? "speaking.status_generating" : "speaking.status_waiting";
            }
        });

        try
        {
            var service = new SpeakingPrepService(AssistantService.Current.AppRoot,
                AssistantService.Current.DataRoot);
            SpeakingPrepOutcome outcome = await service.PrepareAsync(_packId, progress, _cancellation.Token);
            if (outcome.Session is null)
            {
                if (outcome.Errors.Contains(LocalizationService.Current.Translate("common.invalid_credentials_error"), StringComparer.Ordinal) &&
                    MainWindow.Current is { } mainWindow)
                {
                    Close();
                    await mainWindow.PromptForMissingCredentialsAsync();
                    return;
                }
                StatusText.Text = LocalizationService.Current.Translate("speaking.no_audio_generated");
                DiagnosticLogService.Warn("speaking_prep.tts_generation.completed_with_errors",
                    $"pack={_packId} errorCount={outcome.Errors.Count}");
                ProgressCountText.Text = $"0/{_items.Count}";
                ErrorBar.Severity = InfoBarSeverity.Error;
                ErrorBar.Message = string.Join(Environment.NewLine, outcome.Errors.Take(3));
                ErrorBar.IsOpen = true;
                SetHeaderStatus("speaking.status_failed", "SystemFillColorCriticalBrush");
                return;
            }

            OverallProgress.Value = outcome.Session.Items.Count;
            ProgressCountText.Text = $"{outcome.Session.Items.Count}/{_items.Count}";

            if (outcome.Errors.Count > 0)
            {
                DiagnosticLogService.Warn("speaking_prep.tts_generation.completed_with_errors",
                    $"pack={_packId} generated={outcome.Session.Items.Count} errorCount={outcome.Errors.Count}");
                StatusText.Text = LocalizationService.Current.Format("speaking.partial_complete",
                    ("generated", outcome.Session.Items.Count), ("total", _items.Count));
                foreach (SpeakingPrepItem item in _items)
                    if (item.Status != LocalizationService.Current.Translate("speaking.status_completed"))
                        item.Status = "speaking.status_failed";
                ErrorBar.Severity = InfoBarSeverity.Warning;
                ErrorBar.Message = string.Join(Environment.NewLine, outcome.Errors.Take(2));
                ErrorBar.IsOpen = true;
                SetHeaderStatus("speaking.status_partial", "SystemFillColorCautionBrush");
            }
            else
            {
                DiagnosticLogService.Info("speaking_prep.tts_generation.completed",
                    $"pack={_packId} generated={outcome.Session.Items.Count}");
                StatusText.Text = LocalizationService.Current.Format("speaking.auto_speaking_after_generation",
                    ("count", outcome.Session.Items.Count));
                foreach (SpeakingPrepItem item in _items)
                    item.Status = "speaking.status_completed";
                ErrorBar.IsOpen = false;
                _sessionReady = true;
                HeaderStatusText.Visibility = Visibility.Collapsed;
                StartAutoButton.Visibility = Visibility.Visible;
                await StartAutoAsync();
            }
        }
        catch (OperationCanceledException)
        {
            DiagnosticLogService.Info("speaking_prep.tts_generation.cancelled", $"pack={_packId}");
            StatusText.Text = LocalizationService.Current.Translate("speaking.cancelled");
            SetHeaderStatus("speaking.status_cancelled", "TextFillColorSecondaryBrush");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("speaking_prep.tts_generation.failed", ex);
            StatusText.Text = LocalizationService.Current.Translate("speaking.preparation_failed");
            ErrorBar.Severity = InfoBarSeverity.Error;
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            SetHeaderStatus("speaking.status_failed", "SystemFillColorCriticalBrush");
        }
    }

    private void SetHeaderStatus(string text, string brushKey)
    {
        HeaderStatusText.Text = LocalizationService.Current.Translate(text);
        HeaderStatusText.Foreground = SpeakingPrepItem.ResolveBrush(brushKey);
    }

    private async void StartAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_autoRunner.IsRunning)
        {
            DiagnosticLogService.Info("speaking_prep.auto_speaking.stop_requested", $"pack={_packId}");
            _autoRunner.Stop();
            StartAutoButtonText.Text = LocalizationService.Current.Translate("common.start_automatic_speaking");
            StatusText.Text = LocalizationService.Current.Translate("speaking.stopping_auto_speaking");
            return;
        }
        await StartAutoAsync();
    }

    private async Task StartAutoAsync()
    {
        if (!_sessionReady || _autoStartInProgress || _autoRunner.IsRunning) return;
        _autoStartInProgress = true;
        DiagnosticLogService.Info("speaking_prep.auto_speaking.start_requested", $"pack={_packId}");
        StartAutoButton.IsEnabled = false;
        try
        {
            _autoRunnerFailureStatus = null;
            var cable = new VirtualCableService();
            try { await cable.RefreshAsync(); }
            catch (Exception) { }
            if (!cable.IsReady)
            {
                ErrorBar.Severity = InfoBarSeverity.Warning;
                ErrorBar.Message = LocalizationService.Current.Translate(cable.IsAvailable
                    ? "speaking.cable_output_not_enabled" : "speaking.virtual_cable_not_installed");
                ErrorBar.IsOpen = true;
                StatusText.Text = LocalizationService.Current.Translate("speaking.configure_virtual_cable_then_start");
                return;
            }

            AssistantService assistant = AssistantService.Current;
            string[] sessionCandidates =
            [
                Path.Combine(assistant.DataRoot, "_assistant", "speaking_session.json"),
                Path.Combine(assistant.DataRoot, "temp", "speaking_session.json"),
                Path.Combine(assistant.AppRoot, "temp", "speaking_session.json")
            ];
            string sessionPath = sessionCandidates
                .Where(File.Exists)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault() ?? sessionCandidates[0];
            if (!SpeakingAutoRunner.HasUsableAudio(sessionPath, out string audioError))
            {
                DiagnosticLogService.Warn("speaking_prep.auto_speaking.stale_audio", audioError);
                _sessionReady = false;
                StartAutoButton.Visibility = Visibility.Collapsed;
                HeaderStatusText.Visibility = Visibility.Visible;
                SetHeaderStatus("speaking.regenerating_audio", "AccentTextFillColorPrimaryBrush");
                StatusText.Text = LocalizationService.Current.Translate("speaking.previous_audio_cleared_regenerating");
                ErrorBar.IsOpen = false;
                _autoStartInProgress = false;
                await PrepareAsync();
                return;
            }
            string audioInputResult = await DefaultAudioInputService.Current.BeginSessionAsync();
            DefaultAudioInputStatus? audioInput = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                audioInput = await DefaultAudioInputService.Current.GetStatusAsync();
                if (audioInput?.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) == true)
                    break;
                await Task.Delay(250);
            }
            if (audioInput?.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) != true)
            {
                string currentInput = audioInput?.Name ?? LocalizationService.Current.Translate("common.not_available");
                string message = LocalizationService.Current.Format("speaking.virtual_mic_confirmation_error",
                    ("device", currentInput), ("result", audioInputResult));
                DiagnosticLogService.Warn("speaking_prep.auto_speaking.virtual_mic_not_ready", message);
                ErrorBar.Severity = InfoBarSeverity.Error;
                ErrorBar.Message = message;
                ErrorBar.IsOpen = true;
                StatusText.Text = LocalizationService.Current.Translate("speaking.virtual_mic_not_ready");
                return;
            }
            DiagnosticLogService.Info("speaking_prep.auto_speaking.virtual_mic_ready",
                $"device={audioInput.Name}; result={audioInputResult}");
            if (!_autoRunner.Start(assistant.AppRoot, assistant.DataRoot, sessionPath, out string error, _resumeStartIndex))
            {
                DiagnosticLogService.Error("speaking_prep.auto_speaking.start_failed", error);
                ErrorBar.Severity = InfoBarSeverity.Error;
                ErrorBar.Message = error;
                ErrorBar.IsOpen = true;
                if (_autoRunner.LastStartFailureRequiresRegeneration)
                {
                    _sessionReady = false;
                    StartAutoButton.Visibility = Visibility.Collapsed;
                    StatusText.Text = LocalizationService.Current.Translate("speaking.cancelled_no_recording_started");
                }
                else
                {
                    StatusText.Text = LocalizationService.Current.Translate("speaking.generated_auto_speaking_not_started");
                }
                return;
            }

            ErrorBar.IsOpen = false;
            DiagnosticLogService.Info("speaking_prep.auto_speaking.started", $"pack={_packId}");
            StartAutoButtonText.Text = LocalizationService.Current.Translate("common.stop_automatic_speaking");
            StatusText.Text = LocalizationService.Current.Format("speaking.default_mic_waiting_for_ets",
                ("device", audioInput.Name));
        }
        finally
        {
            _autoStartInProgress = false;
            StartAutoButton.IsEnabled = true;
        }
    }

    private sealed record TimingRequest(string PackId, int Index, double RecordingSeconds);

    private async Task RepairTimingAsync()
    {
        StartAutoButton.IsEnabled = false;
        _sessionReady = false;
        try
        {
            string path = _autoRunner.SessionPath ?? throw new InvalidOperationException("Missing timing session");
            string requestPath = path + ".timing-request.json";
            var request = System.Text.Json.JsonSerializer.Deserialize<TimingRequest>(await File.ReadAllTextAsync(requestPath))
                ?? throw new InvalidOperationException("Invalid timing request");
            if (request.PackId != _packId || request.Index < 0 || request.Index >= _items.Count ||
                request.RecordingSeconds <= 0 || request.RecordingSeconds > 600)
                throw new InvalidOperationException("Timing request does not match the active paper");
            StatusText.Text = LocalizationService.Current.Translate("speaking.timing_regenerating");
            ErrorBar.IsOpen = false;
            _items[request.Index].Status = "speaking.status_generating";
            var assistant = AssistantService.Current;
            var service = new SpeakingPrepService(assistant.AppRoot, assistant.DataRoot);
            await service.RepairTimingAsync(path, request.Index, request.RecordingSeconds,
                _cancellation?.Token ?? CancellationToken.None);
            _resumeStartIndex = request.Index;
            _items[request.Index].Status = "speaking.status_completed";
            _sessionReady = true;
            File.Delete(requestPath);
            // Timed mock exams may have moved on while synthesis ran. Resume only after the user returns to this question.
            StatusText.Text = LocalizationService.Current.Translate("speaking.timing_repaired");
            DiagnosticLogService.Info("speaking.timing.repaired", $"pack={_packId} index={request.Index}");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("speaking.timing.repair_failed", ex);
            ErrorBar.Severity = InfoBarSeverity.Error;
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            StatusText.Text = LocalizationService.Current.Translate("speaking.preparation_failed");
        }
        finally { StartAutoButton.IsEnabled = true; }
    }

    private void ApplyTheme()
    {
        AppPreferencesService.Current.ApplyTheme(RootGrid);
        BackdropFallback.Visibility = MicaController.IsSupported()
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateTitleBarColors();
    }

    private void ApplyLocalization()
    {
        LocalizationService localization = LocalizationService.Current;
        localization.ReloadIfChanged();
        localization.Apply(RootGrid);
        Title = _packId.Length == 0
            ? localization.Translate("speaking.default_title")
            : localization.Format("dynamic.speaking_prep_title", ("paperTitle", _paperTitle));
        if (_packId.Length > 0)
            PackText.Text = localization.Format("dynamic.speaking_prep_summary",
                ("paperId", _packId), ("count", _questionCount));
    }

    private void UpdateTitleBarColors()
    {
        try
        {
            AppWindowTitleBar bar = GetAppWindow().TitleBar;
            bool dark = RootGrid.ActualTheme == ElementTheme.Dark ||
                        (RootGrid.ActualTheme == ElementTheme.Default &&
                         Application.Current.RequestedTheme == ApplicationTheme.Dark);
            bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            bar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            bar.ButtonInactiveForegroundColor = dark
                ? Color.FromArgb(0xFF, 148, 163, 184)
                : Color.FromArgb(0xFF, 100, 116, 139);
            bar.ButtonHoverBackgroundColor = dark
                ? Color.FromArgb(40, 255, 255, 255)
                : Color.FromArgb(30, 0, 0, 0);
            bar.ButtonPressedBackgroundColor = dark
                ? Color.FromArgb(64, 255, 255, 255)
                : Color.FromArgb(45, 0, 0, 0);
        }
        catch (Exception)
        {
        }
    }

    private static string Trim(string text) =>
        text.Length <= 46 ? text : text[..46] + "…";

    private AppWindow GetAppWindow()
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        Microsoft.UI.WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(id);
    }

    private void ConfigureWindow()
    {
        try
        {
            AppWindow appWindow = GetAppWindow();
            const int width = 960;
            const int height = 620;
            appWindow.Resize(new SizeInt32(width, height));
            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ProgramIcon.ico"));
            DisplayArea area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;
            appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - width) / 2),
                work.Y + Math.Max(0, (work.Height - height) / 2)));
            UpdateTitleBarColors();
        }
        catch (Exception)
        {
        }
    }
}
