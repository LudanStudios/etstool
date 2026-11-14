using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.UI;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class OverviewPage : Page
{
    private static readonly SolidColorBrush WaitingBadgeBrush = new(Color.FromArgb(31, 148, 163, 184));
    private static readonly SolidColorBrush ActiveBadgeBrush = new(Color.FromArgb(31, 59, 130, 246));
    private static readonly SolidColorBrush CompleteBadgeBrush = new(Color.FromArgb(31, 34, 197, 94));
    private static readonly SolidColorBrush UnansweredChipBrush = new(Color.FromArgb(20, 148, 163, 184));
    private static readonly SolidColorBrush CorrectChipBrush = new(Color.FromArgb(46, 34, 197, 94));
    private static readonly SolidColorBrush WrongChipBrush = new(Color.FromArgb(46, 239, 68, 68));

    private readonly AssistantService _service = AssistantService.Current;
    private readonly CurrentPaperService _paperService = new(AssistantService.Current.DataRoot);
    private readonly AnswerResetService _resetService = new(AssistantService.Current.DataRoot);
    private DispatcherQueueTimer? _timer;
    private CurrentPaperSnapshot? _currentPaper;
    private string _paperSignature = "\0";
    private bool _refreshingPaper;
    private bool? _initializeButtonWrapped;
    private bool _updatingAutomation = true;

    public OverviewPage()
    {
        InitializeComponent();
        DataPathText.Text = _service.DataRoot;
        Loaded += (_, _) =>
        {
            RefreshAutomation();
            RefreshStatus();
            _ = RefreshPaperAsync();
            _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(3);
            _timer.Tick += async (_, _) =>
            {
                RefreshStatus();
                await RefreshPaperAsync();
            };
            _timer.Start();
        };
        Unloaded += (_, _) => _timer?.Stop();
    }

    private void RefreshAutomation()
    {
        _updatingAutomation = true;
        SelectionToggle.IsOn = AppPreferencesService.Current.AutomaticSelectionEnabled;
        SpeakingToggle.IsOn = AppPreferencesService.Current.AutomaticSpeakingEnabled;
        _updatingAutomation = false;
    }

    private async void SelectionToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingAutomation) return;
        try { AppPreferencesService.Current.SetAutomaticSelection(SelectionToggle.IsOn); }
        catch (Exception ex) { RefreshAutomation(); await ShowErrorAsync(ex.Message); }
    }

    private void SpeakingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingAutomation) return;
        AppPreferencesService.Current.SetAutomaticSpeaking(SpeakingToggle.IsOn);
    }

    private void ActionButtonsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wrapInitializeButton = e.NewSize.Width < 720;
        if (_initializeButtonWrapped == wrapInitializeButton) return;

        _initializeButtonWrapped = wrapInitializeButton;
        if (wrapInitializeButton)
        {
            Grid.SetRow(InitializeButton, 1);
            Grid.SetColumn(InitializeButton, 0);
            Grid.SetColumnSpan(InitializeButton, 4);
            InitializeButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            Grid.SetRow(InitializeButton, 0);
            Grid.SetColumn(InitializeButton, 3);
            Grid.SetColumnSpan(InitializeButton, 1);
            InitializeButton.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private void RefreshStatus()
    {
        LocalizationService localization = LocalizationService.Current;
        _service.RefreshEts();
        bool found = _service.EtsExe is not null;
        bool running = found && _service.IsEtsRunning;
        StatusText.Text = localization.Translate(!found
            ? "overview.ets_not_found"
            : running ? "common.ets_running" : "overview.ready_waiting_ets");
        Color color = !found ? Color.FromArgb(255, 220, 38, 38) : running
            ? Color.FromArgb(255, 34, 197, 94) : Color.FromArgb(255, 22, 163, 74);
        StatusDot.Fill = new SolidColorBrush(color);
        EtsPathText.Text = _service.EtsExe ?? localization.Translate("overview.install_ets_scan_again");
        ListenerText.Text = _service.IsAutostartEnabled
            ? localization.Translate("overview.listener_autostart_enabled")
            : localization.Translate("overview.listener_autostart_disabled");
        CredentialStatusText.Text = localization.Format("overview.credentials_status",
            ("deepseek", localization.Translate(ApiKeyStore.HasKey ? "common.configured" : "common.not_configured")),
            ("aliyun", localization.Translate(AliyunTtsCredentialStore.HasCredentials
                ? "common.configured" : "common.not_configured")));
        AppPreferencesService preferences = AppPreferencesService.Current;
        AssistantSettings config = _service.LoadSettings();
        AnswerConfigText.Text = localization.Format("overview.answer_settings_summary",
            ("accuracy", config.Accuracy.ToString("P0", CultureInfo.CurrentCulture)),
            ("skip", config.SkipInterval.ToString("0.##", CultureInfo.CurrentCulture)),
            ("mode", localization.Translate(preferences.SpeakingMode == "flexible"
                ? "common.flexible_mode" : "common.standard_mode")));
        EtsErrorBar.IsOpen = !found;
        if (!string.IsNullOrWhiteSpace(_service.LastError))
        {
            InfoBarPresenter.Show(GeneralInfoBar,
                LocalizationService.Current.Translate("overview.need_attention"), _service.LastError, InfoBarSeverity.Warning);
        }
        else if (found) GeneralInfoBar.IsOpen = false;
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        _service.RefreshEts();
        _service.LastError = string.Empty;
        RefreshStatus();
    }

    private async void ChooseEtsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                CommitButtonText = LocalizationService.Current.Translate("overview.choose_this_folder")
            };
            picker.FileTypeFilter.Add("*");

            MainWindow window = MainWindow.Current
                ?? throw new InvalidOperationException(LocalizationService.Current.Translate("overview.window_not_ready"));
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            EtsDirectoryValidation validation = _service.ValidateEtsDirectory(folder.Path);
            if (!validation.IsValid)
            {
                await ShowDialogAsync("overview.directory_validation_failed", validation.Message, "common.ok");
                return;
            }

            LocalizationService localization = LocalizationService.Current;
            string versionLine = string.IsNullOrWhiteSpace(validation.FileVersion)
                ? string.Empty
                : localization.Format("overview.version_line", ("version", validation.FileVersion));
            var confirm = new ContentDialog
            {
                Title = localization.Translate("overview.directory_validation_confirm_title"),
                Content = localization.Format("overview.ets_installation_info",
                    ("directory", validation.DirectoryPath), ("executable", validation.ExecutablePath),
                    ("versionLine", versionLine)),
                PrimaryButtonText = localization.Translate("overview.apply"),
                CloseButtonText = localization.Translate("common.cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;

            string warnings = _service.ApplyEtsDirectory(validation.DirectoryPath);
            RefreshStatus();
            string title = LocalizationService.Current.Translate(warnings.Length == 0
                ? "overview.ets_directory_applied" : "overview.ets_directory_saved_warning");
            string message = warnings.Length == 0
                ? LocalizationService.Current.Format("overview.ets_directory_future_use",
                    ("executable", validation.ExecutablePath))
                : warnings;
            InfoBarPresenter.Show(GeneralInfoBar, title, message,
                warnings.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private async void InitializeAndOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _service.RefreshEts();
            string warnings = _service.InitializeAndOpen(_service.LoadSettings());
            RefreshStatus();
            string title = LocalizationService.Current.Translate(warnings.Length == 0
                ? "overview.initialization_completed" : "overview.ets_opened_warning");
            string message = warnings.Length == 0
                ? LocalizationService.Current.Translate("overview.listener_started_ets_opening") : warnings;
            InfoBarPresenter.Show(GeneralInfoBar, title, message,
                warnings.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private async void OpenEts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _service.RefreshEts();
            _service.EnsureListener();
            _service.OpenEts();
            InfoBarPresenter.Show(GeneralInfoBar,
                LocalizationService.Current.Translate("overview.ets_started"),
                LocalizationService.Current.Translate("overview.listener_waits_for_ets"),
                InfoBarSeverity.Success);
            RefreshStatus();
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private async Task RefreshPaperAsync()
    {
        if (_refreshingPaper) return;
        _refreshingPaper = true;
        try { ShowPaper(await _paperService.LoadAsync()); }
        catch (Exception) { }
        finally { _refreshingPaper = false; }
    }

    private void ShowPaper(CurrentPaperSnapshot? paper)
    {
        string signature = paper is null
            ? string.Empty
            : $"{paper.PackId}|{paper.Title}|{paper.Total}|{paper.Answered}|{paper.Correct}|{paper.LastActivity:O}|" +
              string.Join(",", paper.Answers.Select(answer => answer.SelectedAnswer)) + "|" +
              string.Join(",", paper.Answers.Select(answer => answer.IsListening ? "L" : "R"));
        if (signature == _paperSignature) return;
        _paperSignature = signature;
        _currentPaper = paper;
        if (paper is null)
        {
            PaperStateText.Text = LocalizationService.Current.Translate("overview.waiting");
            PaperStateBadge.Background = WaitingBadgeBrush;
            PaperTitleText.Text = LocalizationService.Current.Translate("overview.no_paper_detected_yet");
            PaperMetaText.Text = LocalizationService.Current.Translate("overview.open_paper_ets_see_progress");
            PaperProgressPanel.Visibility = Visibility.Collapsed;
            PaperActions.Visibility = Visibility.Collapsed;
            AnswerChips.ItemsSource = null;
            ManualResetButton.IsEnabled = false;
            EmptyPaperActions.Visibility = Visibility.Visible;
            LocalizationService.Current.Apply(EmptyPaperActions);
            return;
        }

        ManualResetButton.IsEnabled = true;
        PaperActions.Visibility = Visibility.Visible;
        EmptyPaperActions.Visibility = Visibility.Collapsed;
        PaperStateText.Text = LocalizationService.Current.Translate(paper.Complete
            ? "common.completed" : "overview.detected");
        PaperStateBadge.Background = paper.Complete ? CompleteBadgeBrush : ActiveBadgeBrush;
        PaperTitleText.Text = paper.Title;
        LocalizationService localization = LocalizationService.Current;
        PaperMetaText.Text = paper.Total == 0
            ? localization.Format("dynamic.paper_summary_no_questions", ("paperId", paper.PackId))
            : paper.LastActivity is DateTime last
                ? localization.Format("dynamic.paper_summary_last_answered",
                    ("paperId", paper.PackId), ("count", paper.Total), ("time", last.ToString("HH:mm:ss")))
                : localization.Format("dynamic.paper_summary", ("paperId", paper.PackId), ("count", paper.Total));
        PaperProgressText.Text = localization.Format("dynamic.answered_count",
            ("answered", paper.Answered), ("total", paper.Total));
        PaperPercentText.Text = paper.Total == 0 ? "0%" : $"{Math.Round(paper.Answered * 100.0 / paper.Total):0}%";
        PaperProgressBar.Maximum = Math.Max(paper.Total, 1);
        PaperProgressBar.Value = paper.Answered;
        int pending = paper.Answered - paper.Correct;
        PaperCorrectText.Text = pending > 0
            ? localization.Format("dynamic.local_answer_summary_with_review",
                ("matching", paper.Correct), ("review", pending))
            : localization.Format("dynamic.local_answer_summary", ("matching", paper.Correct));
        PaperProgressPanel.Visibility = paper.Total == 0 ? Visibility.Collapsed : Visibility.Visible;
        AnswerChips.ItemsSource = paper.Answers.Select(answer => new AnswerChip(
            NumberText: answer.Number.ToString(),
            Answer: answer.CorrectAnswer,
            Tooltip: BuildAnswerTooltip(answer),
            Background: !answer.Answered ? UnansweredChipBrush : answer.Matches ? CorrectChipBrush : WrongChipBrush
        )).ToList();
        LocalizationService.Current.Apply(PaperProgressPanel);
    }

    private static string BuildAnswerTooltip(CurrentPaperAnswer answer)
    {
        LocalizationService localization = LocalizationService.Current;
        string kind = localization.Translate(answer.IsListening
            ? "dynamic.answer_type_listening" : "dynamic.answer_type_choice");
        if (!answer.Answered)
            return localization.Format("dynamic.answer_tooltip_unanswered",
                ("kind", kind), ("number", answer.Number), ("correct", answer.CorrectAnswer));

        return localization.Format("dynamic.answer_tooltip_answered",
            ("kind", kind), ("number", answer.Number), ("correct", answer.CorrectAnswer),
            ("selected", answer.SelectedAnswer),
            ("match", localization.Translate(answer.Matches
                ? "dynamic.answer_match" : "dynamic.answer_mismatch")));
    }

    private async void CopyAnswers_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPaper is null) return;
        try
        {
            string text = PaperCopyService.Format(_currentPaper.PackId,
                _currentPaper.Answers.Select(answer => (answer.Number, answer.CorrectAnswer)), _service.DataRoot, _service.AppRoot);
            if (text.Length == 0) return;
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            InfoBarPresenter.Show(GeneralInfoBar,
                LocalizationService.Current.Translate("overview.answers_copied"),
                LocalizationService.Current.Format("overview.answers_copied_detail",
                    ("count", _currentPaper.Answers.Count + new SpeakingAnswerService(_service.DataRoot).Load(_currentPaper.PackId).Count)), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(LocalizationService.Current.Format("overview.copy_answers_failed",
                ("detail", ex.Message)));
        }
    }

    private async void ManualReset_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPaper is null) return;
        var confirm = new ContentDialog
        {
            Title = LocalizationService.Current.Translate("overview.reset_confirmation_title"),
            Content = LocalizationService.Current.Translate("overview.reset_confirmation_content"),
            PrimaryButtonText = LocalizationService.Current.Translate("overview.start_reset"),
            CloseButtonText = LocalizationService.Current.Translate("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(confirm) != ContentDialogResult.Primary) return;

        ManualResetButton.IsEnabled = false;
        try
        {
            AnswerResetResult result = await _resetService.RequestAsync(_currentPaper.PackId);
            bool applied = result.Status == AnswerResetStatus.Applied;
            InfoBarPresenter.Show(GeneralInfoBar, LocalizationService.Current.Translate(applied
                    ? "overview.answer_progress_reset" : "overview.reset_incomplete"), result.Message,
                applied ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            if (applied) await RefreshPaperAsync();
        }
        finally
        {
            ManualResetButton.IsEnabled = _currentPaper is not null;
        }
    }

    private async void OpenPaperFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPaper is null) return;
        string path = Path.Combine(_service.DataRoot, _currentPaper.PackId);
        try
        {
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException(LocalizationService.Current.Translate("overview.ets_folder_missing"));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private async void EndEts_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = LocalizationService.Current.Translate("overview.terminate_confirmation_title"),
            Content = LocalizationService.Current.Translate("overview.terminate_confirmation_content"),
            PrimaryButtonText = LocalizationService.Current.Translate("common.end_ets"),
            CloseButtonText = LocalizationService.Current.Translate("common.cancel"),
            DefaultButton = ContentDialogButton.None,
            XamlRoot = XamlRoot
        };
        if (await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            int ended = _service.EndEtsProcesses();
            InfoBarPresenter.Show(GeneralInfoBar, LocalizationService.Current.Translate(ended > 0
                    ? "overview.ets_terminated" : "overview.no_ets_running"),
                ended > 0
                    ? LocalizationService.Current.Format("overview.terminated_processes", ("count", ended))
                    : LocalizationService.Current.Translate("overview.no_ets_process_found"),
                InfoBarSeverity.Informational);
            RefreshStatus();
        }
        catch (Exception ex) { await ShowErrorAsync(ex.Message); }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Current?.NavigateToTag("settings");
    }


    private sealed record AnswerChip(string NumberText, string Answer, string Tooltip, Brush Background);

    private async Task ShowDialogAsync(string titleKey, string message, string closeKey)
    {
        LocalizationService localization = LocalizationService.Current;
        var dialog = new ContentDialog
        {
            Title = localization.Translate(titleKey), Content = localization.Translate(message),
            CloseButtonText = localization.Translate(closeKey), XamlRoot = XamlRoot
        };
        await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog);
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = LocalizationService.Current.Translate("common.action_could_not_complete"),
            Content = LocalizationService.Current.Translate(message),
            CloseButtonText = LocalizationService.Current.Translate("common.ok"), XamlRoot = XamlRoot
        };
        await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog);
    }
}
