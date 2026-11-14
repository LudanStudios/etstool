using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ETSAssistant.WinUI3.Services;
using WinRT.Interop;
using Windows.Graphics;
using Windows.UI;
using Windows.ApplicationModel.DataTransfer;
using System.Runtime.InteropServices;

namespace ETSAssistant.WinUI3;

public sealed partial class TrayStatusWindow : Window
{
    private const double WindowCornerRadius = 14;
    private readonly nint _hwnd;
    private bool _visible;
    private bool _closed;
    private long _shownAt;
    private int _width, _height;
    private int _refreshFailures;
    private static readonly NativeSubclassProc FrameCallback = NativeFrameProc;
    private static readonly SolidColorBrush DoneBrush = new(Color.FromArgb(255, 139, 209, 79));
    private static readonly SolidColorBrush PendingBrush = new(Color.FromArgb(255, 118, 118, 118));
    private readonly CurrentPaperService _paperService;
    private readonly SpeakingAnswerService _speakingAnswers;
    private readonly SpeakingSessionStore _speakingSession;
    private readonly DispatcherQueueTimer _refreshTimer;
    private CurrentPaperSnapshot? _paper;
    private bool _refreshing;

    public TrayStatusWindow()
    {
        InitializeComponent();
        AssistantService assistant = AssistantService.Current;
        _paperService = new CurrentPaperService(assistant.DataRoot);
        _speakingAnswers = new SpeakingAnswerService(assistant.DataRoot);
        _speakingSession = new SpeakingSessionStore(assistant.AppRoot);

        nint hwnd = _hwnd = WindowNative.GetWindowHandle(this);
        FlyoutAppWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(hwnd));
        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        FlyoutAppWindow.SetPresenter(presenter);
        FlyoutAppWindow.Resize(new SizeInt32(352, 280));
        FlyoutAppWindow.Title = "ETS Tool 状态";
        FlyoutAppWindow.IsShownInSwitchers = false;
        SetWindowSubclass(hwnd, FrameCallback, 0x54524159, 0);
        // ExtendsContentIntoTitleBar changes the frame setup, so apply DWM styling after it.
        SetNativeWindowStyle(hwnd);
        DispatcherQueue queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("当前线程没有可用的 DispatcherQueue。");
        _refreshTimer = queue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(500);
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_visible && Environment.TickCount64 - _shownAt > 250 && GetForegroundWindow() != _hwnd)
                HidePopup();
            else await RefreshStateAsync();
        };

        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated &&
                Environment.TickCount64 - _shownAt > 200 && GetForegroundWindow() != _hwnd)
                HidePopup();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _refreshTimer.Stop();
            RemoveWindowSubclass(_hwnd, FrameCallback, 0x54524159);
        };
        ApplyLocalization();
    }

    private Microsoft.UI.Windowing.AppWindow FlyoutAppWindow { get; }

    public async Task RefreshStateAsync()
    {
        if (_refreshing || !_visible || _closed) return;
        _refreshing = true;
        try
        {
            var paper = await _paperService.LoadAsync(activeOnly: true);
            if (_closed || !_visible) return;
            // A failed read must not leave a previous paper frozen indefinitely.
            if (paper is null && _paper is not null && ++_refreshFailures < 3) return;
            _refreshFailures = 0;
            _paper = paper;
            ApplyLocalization();
            LocalizationService localization = LocalizationService.Current;
            HeaderText.Text = localization.Translate("tray.title_running");

            if (_paper is null)
            {
                EmptyStateCard.Visibility = Visibility.Visible;
                PaperCard.Visibility = Visibility.Collapsed;
                ChoiceCard.Visibility = Visibility.Collapsed;
                SpeakingCard.Visibility = Visibility.Collapsed;
                EmptyStateTitleText.Text = localization.Translate("overview.no_paper_detected_yet");
                EmptyStateDescriptionText.Text = localization.Translate("overview.open_paper_ets_see_progress");
                PaperHeaderText.Text = localization.Translate("tray.paper_not_found");
                PaperIdText.Text = string.Empty;
                PaperSummaryText.Text = string.Empty;
                CopyAnswersButton.Visibility = Visibility.Collapsed;
                SetDots(ChoiceGrid, 0, 0);
                SetDots(SpeakingGrid, 0, 0);
                ChoiceCountText.Text = "0/0";
                SpeakingCountText.Text = "0/0";
                ChoiceProgressLabel.Text = localization.Format("tray.choice_progress", ("percent", 0));
                SpeakingProgressLabel.Text = localization.Format("tray.speaking_progress", ("percent", 0));
                ChoiceProgressBar.Value = 0;
                SpeakingProgressBar.Value = 0;
                ResizePopup(false);
                return;
            }

            string packId = _paper.PackId;
            var liveTask = TrayLiveProgressReader.ReadAsync(packId);
            var local = await Task.Run(() => (_speakingAnswers.Load(packId), _speakingSession.Load()));
            var live = await liveTask;
            if (_closed || !_visible) return;
            IReadOnlyList<SpeakingQuestion> questions = local.Item1;
            SpeakingSession? session = local.Item2;
            HashSet<int> readyNumbers = session is not null && session.PackId == _paper.PackId
                ? session.Items.Where(item => SpeakingSessionStore.IsUsableWavFile(item.WavPath))
                    .Select(item => item.Number).ToHashSet()
                : new HashSet<int>();
            SpeakingPrepProgressSnapshot? liveProgress = SpeakingPrepService.ActiveProgress;
            bool generating = liveProgress?.PackId == _paper.PackId;
            if (generating && liveProgress!.ReadyNumbers is not null)
                readyNumbers.UnionWith(liveProgress.ReadyNumbers);
            int ready = questions.Count(question => readyNumbers.Contains(question.Number));
            HashSet<int> completedSpeaking = ReadCompletedSpeaking(packId, questions.Count);
            SpeakingProgressRing.IsActive = generating;
            SpeakingProgressRing.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;

            EmptyStateCard.Visibility = Visibility.Collapsed;
            PaperCard.Visibility = Visibility.Visible;
            ChoiceCard.Visibility = Visibility.Visible;
            SpeakingCard.Visibility = Visibility.Visible;
            PaperHeaderText.Text = localization.Translate("tray.paper_detected");
            PaperIdText.Text = $"#{_paper.PackId}";
            PaperSummaryText.Text = localization.Format("tray.paper_summary",
                ("choices", _paper.Total), ("speaking", questions.Count));
            CopyAnswersButton.Visibility = _paper.Answers.Count > 0 || questions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var answeredNumbers = live?.DoneNumbers.Count > 0 ? live.DoneNumbers :
                _paper.Answers.Where(a => a.Answered).Select(a => a.Number).ToHashSet();
            int answered = Math.Clamp(live?.Answered ?? answeredNumbers.Count, 0, _paper.Total);
            ChoiceDoneIcon.Visibility = _paper.Total > 0 && answered == _paper.Total
                ? Visibility.Visible : Visibility.Collapsed;
            SetDots(ChoiceGrid, _paper.Total, answeredNumbers.Count, doneIndices:
                _paper.Answers.Select((a, i) => (a, i)).Where(x => answeredNumbers.Contains(x.a.Number)).Select(x => x.i).ToHashSet());
            SetSpeakingDots(questions, readyNumbers, completedSpeaking);
            ChoiceCountText.Text = $"{answered}/{_paper.Total}";
            SpeakingCountText.Text = $"{ready}/{questions.Count}";
            double choicePercent = _paper.Total == 0 ? 0 : Math.Round(answered * 100.0 / _paper.Total);
            double speakingPercent = questions.Count == 0 ? 0 : Math.Round(ready * 100.0 / questions.Count);
            ChoiceProgressLabel.Text = localization.Format("tray.choice_progress", ("percent", choicePercent));
            SpeakingProgressLabel.Text = localization.Format("tray.speaking_progress", ("percent", speakingPercent));
            ChoiceProgressBar.Value = choicePercent;
            SpeakingProgressBar.Value = speakingPercent;
            ChoiceCard.Visibility = _paper.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
            SpeakingCard.Visibility = questions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ResizePopup(true, _paper.Total, questions.Count);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warn("tray.status.refresh.failed", ex.Message);
        }
        finally { _refreshing = false; }
    }

    public void ShowNearTray()
    {
        if (_closed) return;
        _shownAt = Environment.TickCount64;
        _visible = true;
        ResizePopup(_paper is not null, _paper?.Total ?? 0, SpeakingGrid.Children.Count);
        SetNativeWindowStyle(_hwnd);
        FlyoutAppWindow.Show(true);
        SetWindowPos(_hwnd, new nint(-1), 0, 0, 0, 0, 0x0001 | 0x0002);
        SetForegroundWindow(_hwnd);
        Activate();
        _refreshTimer.Start();
        _ = RefreshStateAsync();
        DiagnosticLogService.Info("tray.status.shown", $"width={_width} height={_height}");
    }

    public void HidePopup()
    {
        if (!_visible) return;
        _visible = false;
        _refreshTimer.Stop();
        FlyoutAppWindow.Hide();
        DiagnosticLogService.Info("tray.status.hidden");
    }

    private void ResizePopup(bool hasPaper, int choices = 0, int speaking = 0)
    {
        nint monitor = MonitorFromPoint(GetCursorPoint(), 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info)) return;
        var work = info.Work;
        double scale = Math.Max(1, GetDpiForWindow(_hwnd) / 96.0);
        int width = Math.Min((int)Math.Round(312 * scale), work.Right - work.Left - 16);
        ContentPanel.Measure(new Windows.Foundation.Size(width / scale, double.PositiveInfinity));
        int height = Math.Min((int)Math.Ceiling(ContentPanel.DesiredSize.Height * scale), work.Bottom - work.Top - 20);
        height = Math.Max((int)(160 * scale), height);
        if (_width == width && _height == height && _visible && GetForegroundWindow() == _hwnd) return;
        _width = width; _height = height;
        FlyoutAppWindow.MoveAndResize(new RectInt32(work.Right - width - 8, work.Bottom - height - 8, width, height));
        int radius = (int)Math.Round(WindowCornerRadius * scale);
        nint region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region != 0 && SetWindowRgn(_hwnd, region, true) == 0) DeleteObject(region);
    }

    private static NativePoint GetCursorPoint() { GetCursorPos(out var point); return point; }

    private static nint NativeFrameProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        // Give XAML the entire client area; DWM must not reserve or paint a one-pixel frame.
        if (message == 0x0083 || message == 0x0085) return 0;
        if (message == 0x0086) return 1;
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private delegate nint NativeSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, NativeSubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, NativeSubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);

    private static void SetNativeWindowStyle(nint hwnd)
    {
        const int GwlStyle = -16;
        const int GwlExStyle = -20;
        const long WsBorder = 0x00800000L;
        const long WsDlgFrame = 0x00400000L;
        const long WsThickFrame = 0x00040000L;
        const long WsCaption = 0x00C00000L;
        const long WsExDlgModalFrame = 0x00000001L;
        const long WsExWindowEdge = 0x00000100L;
        const long WsExClientEdge = 0x00000200L;
        const long WsExStaticEdge = 0x00020000L;
        const uint SwpNoSize = 0x0001;
        const uint SwpNoMove = 0x0002;
        const uint SwpNoZOrder = 0x0004;
        const uint SwpNoActivate = 0x0010;
        const uint SwpFrameChanged = 0x0020;
        const int DwmwaWindowCornerPreference = 33;
        const int DwmwaBorderColor = 34;
        const int DwmwcpRound = 1; // The DPI-scaled native region supplies the complete rounded outline.
        const int DwmColorNone = unchecked((int)0xFFFFFFFE);

        long style = GetWindowLongPtrW(hwnd, GwlStyle).ToInt64();
        style &= ~(WsBorder | WsDlgFrame | WsThickFrame | WsCaption);
        _ = SetWindowLongPtrW(hwnd, GwlStyle, (nint)style);
        long extendedStyle = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
        extendedStyle = (extendedStyle & ~(WsExDlgModalFrame | WsExWindowEdge | WsExClientEdge | WsExStaticEdge | 0x00040000L)) | 0x00000080L;
        _ = SetWindowLongPtrW(hwnd, GwlExStyle, (nint)extendedStyle);
        _ = SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        int cornerPreference = DwmwcpRound;
        int borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference,
            ref cornerPreference, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y,
        int width, int height, uint flags);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute,
        ref int attributeValue, int attributeSize);

    private void ApplyLocalization()
    {
        LocalizationService localization = LocalizationService.Current;
        PaperHeaderText.Text = localization.Translate("tray.paper_not_found");
        ChoiceHeaderText.Text = localization.Translate("tray.choice");
        SpeakingHeaderText.Text = localization.Translate("tray.speaking");
        ChoiceProgressLabel.Text = localization.Format("tray.choice_progress", ("percent", 0));
        SpeakingProgressLabel.Text = localization.Format("tray.speaking_progress", ("percent", 0));
        CopyAnswersButton.Content = localization.Translate("tray.copy_answers");
        HintText.Text = localization.Translate("tray.double_click_hint");
        EmptyStateTitleText.Text = localization.Translate("overview.no_paper_detected_yet");
        EmptyStateDescriptionText.Text = localization.Translate("overview.open_paper_ets_see_progress");
    }

    private HashSet<int> ReadCompletedSpeaking(string packId, int count)
    {
        var done = new HashSet<int>();
        try
        {
            string path = System.IO.Path.Combine(AssistantService.Current.DataRoot, "ets_speaking_auto.log");
            if (!System.IO.File.Exists(path)) return done;
            bool current = false;
            foreach (string line in System.IO.File.ReadLines(path).TakeLast(3000))
            {
                var session = System.Text.RegularExpressions.Regex.Match(line, @"session pack=(\d+) items=(\d+).*?start=(\d+)");
                if (session.Success)
                {
                    current = session.Groups[1].Value == packId && int.Parse(session.Groups[2].Value) == count;
                    if (current && session.Groups[3].Value == "0") done.Clear();
                    continue;
                }
                if (!current) continue;
                var match = System.Text.RegularExpressions.Regex.Match(line, @"\[(\d+)/(\d+)\] (?:stopped|mock recording ended|recording ended)");
                if (match.Success && int.Parse(match.Groups[2].Value) == count) done.Add(int.Parse(match.Groups[1].Value) - 1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or OverflowException) { }
        return done;
    }

    private void SetSpeakingDots(IReadOnlyList<SpeakingQuestion> questions, HashSet<int> ready, HashSet<int> completed)
    {
        SetDots(SpeakingGrid, questions.Count, 0, doneIndices: completed);
        for (int index = 0; index < questions.Count; index++)
            if (!completed.Contains(index) && ready.Contains(questions[index].Number) && SpeakingGrid.Children[index] is Border dot)
                dot.Background = new SolidColorBrush(Color.FromArgb(255, 232, 229, 0));
    }

    private static void SetDots(Grid grid, int total, int completed, int activeIndex = -1, HashSet<int>? doneIndices = null)
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        if (total <= 0) return;

        int columns = Math.Min(10, total);
        int rows = (total + columns - 1) / columns;
        for (int column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int row = 0; row < rows; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });

        for (int index = 0; index < total; index++)
        {
            var dot = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 18,
                CornerRadius = new CornerRadius(5),
                Background = (doneIndices?.Contains(index) ?? (index < completed)) ? DoneBrush : index == activeIndex
                    ? new SolidColorBrush(Color.FromArgb(255, 232, 229, 0))
                    : PendingBrush,
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 69, 69, 69)),
                BorderThickness = new Thickness(1)
            };
            Grid.SetColumn(dot, index % columns);
            Grid.SetRow(dot, index / columns);
            grid.Children.Add(dot);
        }
    }

    private void CopyAnswers_Click(object sender, RoutedEventArgs e)
    {
        if (_paper is null) return;
        try
        {
            var assistant = AssistantService.Current;
            string text = PaperCopyService.Format(_paper.PackId,
                _paper.Answers.Select(a => (a.Number, a.CorrectAnswer)), assistant.DataRoot, assistant.AppRoot);
            if (text.Length == 0) return;
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            DiagnosticLogService.Info("tray.status.answers_copied", $"paper={_paper.PackId}");
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Warn("tray.status.copy_answers.failed", ex.Message);
        }
    }
}
