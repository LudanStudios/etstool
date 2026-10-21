using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3;

public sealed class SpeakingPrepItem : INotifyPropertyChanged
{
    public SpeakingPrepItem(int number, string title)
    {
        Number = number.ToString();
        Title = title;
    }

    public string Number { get; }
    public string Title { get; }
    private string _status = "等待";
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class SpeakingPrepWindow : Window
{
    public static SpeakingPrepWindow? ActiveWindow { get; private set; }

    private readonly ObservableCollection<SpeakingPrepItem> _items = new();
    private string _packId = string.Empty;
    private CancellationTokenSource? _cancellation;

    public SpeakingPrepWindow()
    {
        InitializeComponent();
        ActiveWindow = this;
        Title = "口语题准备";
        QuestionList.ItemsSource = _items;
        ConfigureWindow();
        Closed += (_, _) =>
        {
            _cancellation?.Cancel();
            if (ActiveWindow == this) ActiveWindow = null;
        };
    }

    public void Start(string packId, string paperTitle, IReadOnlyList<SpeakingQuestion> questions)
    {
        _packId = packId;
        Title = $"口语题准备 · {paperTitle}";
        TitleText.Text = $"口语题准备 · {paperTitle}";
        PackText.Text = $"试卷 {packId} · {questions.Count} 道口语题";
        _items.Clear();
        foreach (SpeakingQuestion question in questions)
            _items.Add(new SpeakingPrepItem(question.Number, $"{question.Type} · {Trim(question.Prompt)}"));

        OverallProgress.Minimum = 0;
        OverallProgress.Maximum = Math.Max(1, questions.Count);
        OverallProgress.Value = 0;
        StatusText.Text = "开始生成 TTS 音频…";
        ErrorText.Text = string.Empty;
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
                ErrorText.Text = string.Join(Environment.NewLine, outcome.Errors.Take(3));
                return;
            }

            OverallProgress.Value = outcome.Session.Items.Count;
            StatusText.Text = $"完成：{outcome.Session.Items.Count} 条音频，可以开始自动口语。";
            foreach (SpeakingPrepItem item in _items)
                if (item.Status != "完成") item.Status = "失败";
            if (outcome.Errors.Count > 0)
                ErrorText.Text = string.Join(Environment.NewLine, outcome.Errors.Take(2));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "准备失败。";
            ErrorText.Text = ex.Message;
        }
    }

    private static string Trim(string text) =>
        text.Length <= 40 ? text : text[..40] + "…";

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ConfigureWindow()
    {
        try
        {
            nint hwnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            const int width = 1000;
            const int height = 450;
            appWindow.Resize(new SizeInt32(width, height));
            appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ProgramIcon.ico"));
            DisplayArea area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;
            appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - width) / 2),
                work.Y + Math.Max(0, work.Height - height - 48)));
        }
        catch (Exception)
        {
        }
    }
}
