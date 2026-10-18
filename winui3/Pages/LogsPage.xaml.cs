using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class LogsPage : Page
{
    private readonly AssistantService _service = AssistantService.Current;
    private DispatcherQueueTimer? _timer;

    public LogsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Refresh();
            _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer?.Stop();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        LogText.Text = _service.ReadRecentLogs();
        LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, true);
    }
}
