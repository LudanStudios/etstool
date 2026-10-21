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
        RootGrid.Loaded += async (_, _) => await ShowCredentialsReminderAsync();
    }

    private bool _credentialReminderShown;

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
