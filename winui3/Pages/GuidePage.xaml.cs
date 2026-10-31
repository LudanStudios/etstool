using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class GuidePage : Page
{
    private readonly AssistantService _service = AssistantService.Current;

    public GuidePage() => InitializeComponent();

    private async void OpenReadme_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = _service.ReadmePath;
            if (!File.Exists(path)) throw new FileNotFoundException("找不到 README 使用说明。", path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "无法打开说明", Content = ex.Message, CloseButtonText = "知道了", XamlRoot = XamlRoot
            };
            await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog);
        }
    }
}
