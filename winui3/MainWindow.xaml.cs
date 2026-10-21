using Microsoft.UI;
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
    internal static MainWindow? Current { get; private set; }
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
        Closed += (_, _) => Current = null;
        Title = "ETS Tool by @azursmelt";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        SetWindowSize(1120, 800);
        SetAppIcon();
        SetTitleBarColors();

        AppPreferencesService preferences = AppPreferencesService.Current;
        LocalizationService.Current.Load(preferences.Language);
        ApplyTheme();
        ApplyLocalization();
        PageFrame.Navigated += (_, args) =>
        {
            if (args.Content is DependencyObject page)
                DispatcherQueue.TryEnqueue(() => LocalizationService.Current.Apply(page));
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
            await ShowCredentialsReminderAsync();
            await CheckVirtualCableAsync();
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

    private bool _credentialReminderShown;

    private async Task CheckVirtualCableAsync()
    {
        AppPreferencesService preferences = AppPreferencesService.Current;
        if (preferences.VirtualCableReminderShown) return;

        var cable = new VirtualCableService();
        try { await cable.RefreshAsync(); }
        catch (Exception) { return; }
        if (cable.IsReady) return;

        string message = !cable.IsAvailable
            ? "未检测到 VB-CABLE 虚拟声卡。自动口语需要把 TTS 播放到 CABLE Input，再由 ETS 从 CABLE Output 录音。程序目录里已附带安装包，可在“测试工具 → 自动口语测试”里一键安装。"
            : "检测到 CABLE Input，但没有找到录音端 CABLE Output。请打开录音设备，启用 CABLE Output，并把 ETS 麦克风设为它。";
        var dialog = new ContentDialog
        {
            Title = "虚拟声卡未配置",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = cable.IsAvailable ? "打开录音设备" : "去测试工具",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        ContentDialogResult result = await dialog.ShowAsync();
        preferences.MarkVirtualCableReminderShown();
        if (result != ContentDialogResult.Primary) return;
        if (cable.IsAvailable)
        {
            VirtualCableSetupService.OpenRecordingDevices();
            return;
        }
        foreach (object item in RootNavigation.MenuItems)
        {
            if (item is NavigationViewItem nav && (nav.Tag as string) == "tools")
            {
                RootNavigation.SelectedItem = nav;
                break;
            }
        }
    }

    private async Task ShowCredentialsReminderAsync()
    {
        if (_credentialReminderShown) return;
        _credentialReminderShown = true;
        AppPreferencesService preferences = AppPreferencesService.Current;
        if (preferences.CredentialsReminderShown) return;

        bool hasDeepSeek = ApiKeyStore.HasKey;
        bool hasAliyun = AliyunTtsCredentialStore.HasCredentials;
        if (hasDeepSeek && hasAliyun) return;

        var lines = new List<string>();
        if (!hasDeepSeek) lines.Add("• DeepSeek API Key（生成模式需要）");
        if (!hasAliyun) lines.Add("• 阿里云 TTS 凭证（语音合成需要）");

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "检测到以下凭据还没有填写：", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, lines),
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = "标答模式可以跳过 DeepSeek，但 TTS 仍需要阿里云凭证。",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        });

        var dialog = new ContentDialog
        {
            Title = "完善 API 凭据",
            Content = content,
            PrimaryButtonText = "去填写",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        ContentDialogResult result = await dialog.ShowAsync();
        preferences.MarkCredentialsReminderShown();
        if (result != ContentDialogResult.Primary) return;
        foreach (object item in RootNavigation.MenuItems)
        {
            if (item is NavigationViewItem nav && (nav.Tag as string) == "settings")
            {
                RootNavigation.SelectedItem = nav;
                break;
            }
        }
    }

    private void ApplyTheme()
    {
        if (Content is FrameworkElement root) AppPreferencesService.Current.ApplyTheme(root);
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
            "preferences" => typeof(Pages.PreferencesPage),
            "about" => typeof(Pages.AboutPage),
            _ => null
        };
        if (page is not null && PageFrame.CurrentSourcePageType != page) PageFrame.Navigate(page);
    }

    private void SetWindowSize(int width, int height)
    {
        nint hwnd = WindowNative.GetWindowHandle(this);
        WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow appWindow = AppWindow.GetFromWindowId(id);
        double scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96.0;
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
