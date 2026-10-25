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
    private string _status = "等待";
    private string _statusGlyph = "\uE823";
    private Brush _statusBrush = ResolveBrush("TextFillColorTertiaryBrush");

    public SpeakingPrepItem(int number, string kind, string prompt)
    {
        Number = number.ToString();
        Kind = kind;
        Prompt = prompt;
    }

    public string Number { get; }
    public string Kind { get; }
    public string Prompt { get; }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            (_statusGlyph, _statusBrush) = value switch
            {
                "完成" => ("\uE73E", ResolveBrush("SystemFillColorSuccessBrush")),
                "生成中" => ("\uE895", ResolveBrush("AccentTextFillColorPrimaryBrush")),
                "失败" => ("\uEA39", ResolveBrush("SystemFillColorCriticalBrush")),
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
    private CancellationTokenSource? _cancellation;
    private bool _sessionReady;
    private bool _autoStartInProgress;

    public SpeakingPrepWindow()
    {
        InitializeComponent();
        ActiveWindow = this;
        Title = "口语题准备";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        QuestionList.ItemsSource = _items;
        ApplyTheme();
        ConfigureWindow();
        AppPreferencesService.Current.ThemeChanged += ApplyTheme;
        _autoRunner.Output += line => DispatcherQueue.TryEnqueue(() => StatusText.Text = line);
        _autoRunner.Exited += () => DispatcherQueue.TryEnqueue(() =>
        {
            StartAutoButtonText.Text = "开始自动口语";
            StatusText.Text = "自动口语已结束。";
        });
        Closed += (_, _) =>
        {
            AppPreferencesService.Current.ThemeChanged -= ApplyTheme;
            _autoRunner.Stop();
            _cancellation?.Cancel();
            if (ActiveWindow == this) ActiveWindow = null;
        };
    }

    public void Start(string packId, string paperTitle, IReadOnlyList<SpeakingQuestion> questions)
    {
        _packId = packId;
        Title = $"口语题准备 · {paperTitle}";
        PackText.Text = $"试卷 {packId} · {questions.Count} 道口语题";
        _items.Clear();
        foreach (SpeakingQuestion question in questions)
            _items.Add(new SpeakingPrepItem(question.Number, question.Type, Trim(question.Prompt)));

        OverallProgress.Minimum = 0;
        OverallProgress.Maximum = Math.Max(1, questions.Count);
        OverallProgress.Value = 0;
        ProgressCountText.Text = $"0/{questions.Count}";
        StatusText.Text = "开始生成 TTS 音频…";
        ErrorBar.IsOpen = false;
        _sessionReady = false;
        StartAutoButton.Visibility = Visibility.Collapsed;
        HeaderStatusText.Visibility = Visibility.Visible;
        StartAutoButtonText.Text = "开始自动口语";
        SetHeaderStatus("生成中", "AccentTextFillColorPrimaryBrush");
        _ = PrepareAsync();
    }

    private async Task PrepareAsync()
    {
        _cancellation?.Cancel();
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<SpeakingPrepProgress>(update =>
        {
            OverallProgress.Maximum = Math.Max(1, update.Total);
            OverallProgress.Value = Math.Min(update.Completed, update.Total);
            ProgressCountText.Text = $"{Math.Min(update.Completed, update.Total)}/{update.Total}";
            StatusText.Text = update.Message;
            for (int index = 0; index < _items.Count; index++)
            {
                _items[index].Status = index < update.Completed
                    ? "完成"
                    : index == update.Completed ? "生成中" : "等待";
            }
        });

        try
        {
            var service = new SpeakingPrepService(AssistantService.Current.AppRoot,
                AssistantService.Current.DataRoot);
            SpeakingPrepOutcome outcome = await service.PrepareAsync(_packId, progress, _cancellation.Token);
            if (outcome.Session is null)
            {
                StatusText.Text = "没有生成音频。";
                ProgressCountText.Text = $"0/{_items.Count}";
                ErrorBar.Severity = InfoBarSeverity.Error;
                ErrorBar.Message = string.Join(Environment.NewLine, outcome.Errors.Take(3));
                ErrorBar.IsOpen = true;
                SetHeaderStatus("失败", "SystemFillColorCriticalBrush");
                return;
            }

            OverallProgress.Value = outcome.Session.Items.Count;
            ProgressCountText.Text = $"{outcome.Session.Items.Count}/{_items.Count}";

            if (outcome.Errors.Count > 0)
            {
                StatusText.Text = $"部分完成：{outcome.Session.Items.Count}/{_items.Count} 条音频；自动口语未启动。";
                foreach (SpeakingPrepItem item in _items)
                    if (item.Status != "完成") item.Status = "失败";
                ErrorBar.Severity = InfoBarSeverity.Warning;
                ErrorBar.Message = string.Join(Environment.NewLine, outcome.Errors.Take(2));
                ErrorBar.IsOpen = true;
                SetHeaderStatus("部分完成", "SystemFillColorCautionBrush");
            }
            else
            {
                StatusText.Text = $"生成完成：{outcome.Session.Items.Count} 条音频，正在自动启动口语。";
                foreach (SpeakingPrepItem item in _items)
                    item.Status = "完成";
                ErrorBar.IsOpen = false;
                _sessionReady = true;
                HeaderStatusText.Visibility = Visibility.Collapsed;
                StartAutoButton.Visibility = Visibility.Visible;
                await StartAutoAsync();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
            SetHeaderStatus("已取消", "TextFillColorSecondaryBrush");
        }
        catch (Exception ex)
        {
            StatusText.Text = "准备失败。";
            ErrorBar.Severity = InfoBarSeverity.Error;
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            SetHeaderStatus("失败", "SystemFillColorCriticalBrush");
        }
    }

    private void SetHeaderStatus(string text, string brushKey)
    {
        HeaderStatusText.Text = text;
        HeaderStatusText.Foreground = SpeakingPrepItem.ResolveBrush(brushKey);
    }

    private async void StartAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_autoRunner.IsRunning)
        {
            _autoRunner.Stop();
            StartAutoButtonText.Text = "开始自动口语";
            StatusText.Text = "正在停止自动口语…";
            return;
        }
        await StartAutoAsync();
    }

    private async Task StartAutoAsync()
    {
        if (!_sessionReady || _autoStartInProgress || _autoRunner.IsRunning) return;
        _autoStartInProgress = true;
        StartAutoButton.IsEnabled = false;
        try
        {
            var cable = new VirtualCableService();
            try { await cable.RefreshAsync(); }
            catch (Exception) { }
            if (!cable.IsReady)
            {
                ErrorBar.Severity = InfoBarSeverity.Warning;
                ErrorBar.Message = cable.IsAvailable
                    ? "检测到 CABLE Input，但没有启用录音端 CABLE Output。请打开录音设备启用它，并把 ETS 麦克风设为 CABLE Output。"
                    : "未安装 VB-CABLE 虚拟声卡。请到“测试工具 → 自动口语测试”点“安装/配置虚拟声卡”。";
                ErrorBar.IsOpen = true;
                StatusText.Text = "音频已生成；配置虚拟声卡后可启动自动口语。";
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
            if (!_autoRunner.Start(assistant.AppRoot, assistant.DataRoot, sessionPath, out string error))
            {
                ErrorBar.Severity = InfoBarSeverity.Error;
                ErrorBar.Message = error;
                ErrorBar.IsOpen = true;
                StatusText.Text = "音频已生成，但自动口语未能启动。";
                return;
            }

            ErrorBar.IsOpen = false;
            StartAutoButtonText.Text = "停止自动口语";
            StatusText.Text = "自动口语已启动，等待 ETS 进入录音阶段…";
        }
        finally
        {
            _autoStartInProgress = false;
            StartAutoButton.IsEnabled = true;
        }
    }

    private void ApplyTheme()
    {
        AppPreferencesService.Current.ApplyTheme(RootGrid);
        BackdropFallback.Visibility = MicaController.IsSupported()
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateTitleBarColors();
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
