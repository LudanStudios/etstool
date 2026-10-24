using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using ETSAssistant.WinUI3.Services;
using WinRT.Interop;
using Windows.Graphics;

namespace ETSAssistant.WinUI3;

public sealed partial class MainWindow : Window
{
    internal new static MainWindow? Current { get; private set; }
    internal Microsoft.UI.Xaml.Controls.WebView2 AnalysisPdfRenderer => SharedPdfRendererControl;
    internal bool IsShowingPage(Page page) => ReferenceEquals(PageFrame.Content, page);
    private readonly CurrentPaperService _paperService = new(AssistantService.Current.DataRoot);
    private readonly SpeakingAnswerService _speakingAnswers = new(AssistantService.Current.DataRoot);
    private DispatcherQueueTimer? _paperTimer;
    private string? _lastPrepPackId;
    private bool _checkingSpeakingPaper;
    private SpeakingPrepWindow? _prepWindow;

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        AnalysisActivityService.Current.Changed += UpdateActivityNavigation;
        UpdateActivityNavigation();
        Closed += async (_, _) =>
        {
            AnalysisActivityService.Current.Changed -= UpdateActivityNavigation;
            try { await DefaultAudioInputService.Current.EndSessionAsync(); }
            catch (Exception) { }
            Current = null;
        };
        Title = "ETS Tool by @azursmelt";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        SetWindowSize(1120, 800);
        SetAppIcon();
        SetTitleBarColors();

        AppPreferencesService preferences = AppPreferencesService.Current;
        foreach (object item in RootNavigation.MenuItems)
        {
            if (item is NavigationViewItem nav && (nav.Tag as string) == "tools")
                nav.Visibility = preferences.DeveloperToolsUnlocked ? Visibility.Visible : Visibility.Collapsed;
        }
        LocalizationService.Current.Load(preferences.Language);
        ApplyTheme();
        ApplyLocalization();
        PageFrame.Navigated += (_, args) =>
        {
            if (args.Content is DependencyObject page)
                DispatcherQueue.TryEnqueue(() =>
                {
                    LocalizationService.Current.Apply(page);
                    if (page is Page responsivePage)
                        ResponsiveLayoutService.Attach(responsivePage);
                });
        };
        preferences.ThemeChanged += ApplyTheme;
        preferences.LanguageChanged += OnLanguageChanged;

        DispatcherQueueTimer localizationTimer = DispatcherQueue.CreateTimer();
        localizationTimer.Interval = TimeSpan.FromSeconds(1);
        localizationTimer.Tick += (_, _) => ApplyLocalization();
        localizationTimer.Start();
        Closed += (_, _) => localizationTimer.Stop();

        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        PageFrame.Navigate(typeof(Pages.OverviewPage));
        RootGrid.Loaded += async (_, _) =>
        {
            UpdatePaneToggleButtonStyle(RootNavigation.DisplayMode);
            _ = await DefaultAudioInputService.Current.BeginSessionAsync();
            await ShowWelcomeAsync();
            await CheckVirtualCableAsync();
            await CheckStartupForUpdatesAsync();
        };

        _paperTimer = DispatcherQueue.CreateTimer();
        _paperTimer.Interval = TimeSpan.FromSeconds(3);
        _paperTimer.Tick += async (_, _) => await CheckSpeakingPaperAsync();
        _paperTimer.Start();
        Closed += (_, _) => _paperTimer.Stop();
    }

    private async Task CheckSpeakingPaperAsync()
    {
        if (_checkingSpeakingPaper) return;
        _checkingSpeakingPaper = true;
        try
        {
            string? packId = await CurrentPaperService.FindActivePackIdAsync();
            if (packId is null)
            {
                _lastPrepPackId = null;
                return;
            }
            if (string.Equals(packId, _lastPrepPackId, StringComparison.Ordinal)) return;
            _lastPrepPackId = packId;

            IReadOnlyList<SpeakingQuestion> questions = _speakingAnswers.Load(packId);
            if (questions.Count == 0) return;

            var sessionStore = new SpeakingSessionStore(AssistantService.Current.AppRoot);
            SpeakingSession? existing = sessionStore.Load();
            if (existing is not null && existing.PackId == packId && existing.Items.Count > 0 &&
                existing.Items.All(item => File.Exists(item.WavPath)))
                return;

            string title = _speakingAnswers.LoadAllPapers()
                .FirstOrDefault(paper => paper.PackId == packId)?.Title ?? $"试卷 {packId}";
            ShowSpeakingPrep(packId, title, questions);
        }
        catch (Exception)
        {
            // A transient DevTools/IO failure should not break the main window.
        }
        finally
        {
            _checkingSpeakingPaper = false;
        }
    }

    private void ShowSpeakingPrep(string packId, string title, IReadOnlyList<SpeakingQuestion> questions)
    {
        try { _prepWindow?.Close(); }
        catch (Exception) { }
        var window = new SpeakingPrepWindow();
        window.Closed += (_, _) =>
        {
            if (_prepWindow == window) _prepWindow = null;
        };
        _prepWindow = window;
        window.Start(packId, title, questions);
        window.Activate();
    }

    private async Task CheckVirtualCableAsync()
    {
        AppPreferencesService preferences = AppPreferencesService.Current;
        if (preferences.VirtualCableReminderShown) return;

        var cable = new VirtualCableService();
        try { await cable.RefreshAsync(); }
        catch (Exception) { return; }
        if (cable.IsReady) return;

        string message = !cable.IsAvailable
            ? "未检测到 VB-CABLE 虚拟声卡。需要时可在“关于”中连续点击版本号 7 次，确认后进入开发者工具安装或检查虚拟麦克风。"
            : "检测到 CABLE Input，但没有找到录音端 CABLE Output。请检查录音设备是否启用。";
        var dialog = new ContentDialog
        {
            Title = "虚拟声卡未配置",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "打开录音设备",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        ContentDialogResult result = await dialog.ShowAsync();
        preferences.MarkVirtualCableReminderShown();
        if (result != ContentDialogResult.Primary) return;
        VirtualCableSetupService.OpenRecordingDevices();
    }

    private async Task ShowWelcomeAsync()
    {
        AppPreferencesService preferences = AppPreferencesService.Current;
        if (preferences.WelcomeCompleted) return;

        bool hasDeepSeek = ApiKeyStore.HasKey;
        bool hasAliyun = AliyunTtsCredentialStore.HasCredentials;
        if (hasDeepSeek && hasAliyun)
        {
            preferences.MarkWelcomeCompleted();
            return;
        }

        var lines = new List<string>();
        if (!hasDeepSeek) lines.Add("• DeepSeek API Key（口语生成）");
        if (!hasAliyun) lines.Add("• 阿里云 TTS RAM 凭据与 AppID（语音合成）");

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "欢迎使用 ETS Tool。以下凭据尚未配置：", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, lines),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "如果选择体验模式，缺少个人凭据的功能会把题目、听力内容或 TTS 文本发送到公用 Cloudflare Worker。听力每设备每天最多 10 次完整请求、同一 IP 每天最多 30 次；口语与 TTS 也有每日限额。你可以先填写自己的凭据，也可以之后在偏好设置中启用体验模式。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            Title = "欢迎使用 ETS Tool",
            Content = content,
            PrimaryButtonText = "填写凭据",
            SecondaryButtonText = "启用体验模式",
            CloseButtonText = "暂不启用",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        ContentDialogResult result = await dialog.ShowAsync();
        preferences.MarkCredentialsReminderShown();
        preferences.MarkWelcomeCompleted();
        if (result == ContentDialogResult.Secondary)
        {
            preferences.SetExperienceMode(true);
            return;
        }
        if (result != ContentDialogResult.Primary) return;
        NavigateToTag("settings");
        if (PageFrame.Content is Pages.SettingsPage settingsPage) settingsPage.ExpandCredentials();
    }

    private async Task CheckStartupForUpdatesAsync()
    {
        AppPreferencesService preferences = AppPreferencesService.Current;
        if (!preferences.AutoUpdateEnabled ||
            preferences.LastUpdateCheckUtc is DateTimeOffset last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24))
            return;
        try
        {
            UpdateCheckResult result = await new GitHubUpdateService().CheckAsync();
            preferences.MarkUpdateCheck(result.CheckedAt);
            if (!result.UpdateAvailable || RootGrid.XamlRoot is null) return;
            var dialog = new ContentDialog
            {
                Title = "发现 ETS Tool 更新",
                Content = result.Message + "\n\n是否打开 GitHub 查看？",
                PrimaryButtonText = "查看更新",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.Url) { UseShellExecute = true });
        }
        catch (Exception) { /* A transient network failure must not interrupt startup. */ }
    }

    public async Task UnlockDeveloperToolsAsync()
    {
        AppPreferencesService preferences = AppPreferencesService.Current;
        if (preferences.DeveloperToolsUnlocked)
        {
            NavigateToTag("tools");
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "显示开发者工具？",
            Content = "开发者工具包含抓包、详细日志、虚拟麦克风操作和完全重置。完全重置会删除本地配置与凭据。确定显示该页面？",
            PrimaryButtonText = "显示开发者工具",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.None,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        preferences.UnlockDeveloperTools();
        NavigateToTag("tools");
    }

    public void NavigateToTag(string tag)
    {
        foreach (object item in RootNavigation.MenuItems.Concat(RootNavigation.FooterMenuItems))
        {
            if (item is NavigationViewItem nav && string.Equals(nav.Tag as string, tag, StringComparison.Ordinal))
            {
                if (tag == "tools") nav.Visibility = Visibility.Visible;
                RootNavigation.SelectedItem = nav;
                break;
            }
        }
    }

    private void ApplyTheme()
    {
        if (Content is FrameworkElement root)
        {
            AppPreferencesService.Current.ApplyTheme(root);
            BackdropFallback.Visibility = MicaController.IsSupported()
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void ApplyLocalization()
    {
        LocalizationService localization = LocalizationService.Current;
        localization.Apply(Content as DependencyObject);
        foreach (object item in RootNavigation.MenuItems) localization.Apply(item as DependencyObject);
        foreach (object item in RootNavigation.FooterMenuItems) localization.Apply(item as DependencyObject);
    }

    private void OnLanguageChanged()
    {
        ApplyLocalization();
        if (PageFrame.CurrentSourcePageType is Type page) PageFrame.Navigate(page);
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        Type? page = item.Tag?.ToString() switch
        {
            "overview" => typeof(Pages.OverviewPage),
            "papers" => typeof(Pages.PapersPage),
            "settings" => typeof(Pages.SettingsPage),
            "logs" => typeof(Pages.LogsPage),
            "tools" => typeof(Pages.ToolsPage),
            "guide" => typeof(Pages.GuidePage),
            "activities" => typeof(Pages.ActivityPage),
            "preferences" => typeof(Pages.PreferencesPage),
            "about" => typeof(Pages.AboutPage),
            _ => null
        };
        if (page is not null && PageFrame.CurrentSourcePageType != page) PageFrame.Navigate(page);
    }

    private void UpdateActivityNavigation()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(UpdateActivityNavigation);
            return;
        }
        int running = AnalysisActivityService.Current.RunningCount;
        ActivityNavigationRing.IsActive = running > 0;
        ToolTipService.SetToolTip(ActivitiesNavigationItem,
            running == 0 ? "查看当前活动和最近任务" : $"{running} 项活动正在进行");
    }

    private void RootNavigation_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        UpdatePaneToggleButtonStyle(args.DisplayMode);
    }

    private void UpdatePaneToggleButtonStyle(NavigationViewDisplayMode displayMode)
    {
        if (displayMode == NavigationViewDisplayMode.Expanded)
        {
            RootNavigation.ClearValue(NavigationView.PaneToggleButtonStyleProperty);
            return;
        }

        if (RootNavigation.Resources["WindowedPaneToggleButtonStyle"] is Style style)
            RootNavigation.PaneToggleButtonStyle = style;
    }

    private void SetWindowSize(int width, int height)
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow appWindow = AppWindow.GetFromWindowId(id);
        double scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96.0;
        if (appWindow.Presenter is OverlappedPresenter presenter)
            presenter.PreferredMinimumWidth = (int)Math.Round(450 * scale);
        RectInt32 workArea = DisplayArea.GetFromWindowId(id, DisplayAreaFallback.Primary).WorkArea;
        int margin = (int)Math.Round(24 * scale);
        int pixelWidth = Math.Min((int)Math.Round(width * scale), Math.Max(1, workArea.Width - margin * 2));
        int pixelHeight = Math.Min((int)Math.Round(height * scale), Math.Max(1, workArea.Height - margin * 2));
        appWindow.MoveAndResize(new RectInt32(
            workArea.X + (workArea.Width - pixelWidth) / 2,
            workArea.Y + (workArea.Height - pixelHeight) / 2,
            pixelWidth, pixelHeight));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    private void SetAppIcon()
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow.GetFromWindowId(id).SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ProgramIcon.ico"));
    }

    private void SetTitleBarColors()
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindowTitleBar titleBar = AppWindow.GetFromWindowId(id).TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(38, 255, 255, 255);
        titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(64, 255, 255, 255);
    }
}
