using System.Diagnostics;
using System.Runtime.InteropServices;
using ETSAssistant.WinUI3.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.Foundation;

namespace ETSAssistant.WinUI3.Services;

public static class PdfAnalysisDocumentService
{
    private static readonly SemaphoreSlim RenderGate = new(1, 1);
    private static readonly SemaphoreSlim PreviewGate = new(1, 1);
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string GetDownloadsFolderPath()
    {
        Guid folderId = DownloadsFolderId;
        int result = SHGetKnownFolderPath(ref folderId, 0, nint.Zero, out nint pathPointer);
        if (result != 0) Marshal.ThrowExceptionForHR(result);
        try
        {
            string path = Marshal.PtrToStringUni(pathPointer) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path)) throw new IOException(LocalizationService.Current.Translate("pdf.downloads_folder_path_missing"));
            return path;
        }
        finally { Marshal.FreeCoTaskMem(pathPointer); }
    }

    public static async Task SaveMarkdownAsync(string markdown, string path)
    {
        await RenderGate.WaitAsync();
        try
        {
            MainWindow window = MainWindow.Current ?? throw new InvalidOperationException(LocalizationService.Current.Translate("pdf.main_window_closed"));
            WebView2 renderer = window.AnalysisPdfRenderer;
            await renderer.EnsureCoreWebView2Async();
            string body = MarkdownHtmlRenderer.ToHtml(markdown);
            string html = "<!doctype html><html><head><meta charset=\"utf-8\"><style>" +
                          "@page{size:A4;margin:18mm 18mm 20mm}*{box-sizing:border-box}" +
                          "body{font-family:'Segoe UI','Microsoft YaHei',sans-serif;color:#243044;font-size:10.5pt;line-height:1.72}" +
                          "h1{font-size:23pt;line-height:1.25;color:#17365d;margin:0 0 20pt;padding:0 0 10pt;border-bottom:2px solid #d8e3f0}" +
                          "h2{font-size:16pt;line-height:1.35;color:#23466c;margin:24pt 0 10pt;padding-left:9pt;border-left:3pt solid #5b83b4;break-after:avoid}" +
                          "h3{font-size:12.5pt;color:#2f4d6b;margin:18pt 0 7pt;break-after:avoid}" +
                          "h4,h5,h6{font-size:11pt;color:#334b64;margin:14pt 0 6pt;break-after:avoid}" +
                          "p{margin:0 0 9pt;orphans:3;widows:3}ul,ol{padding-left:22pt;margin:4pt 0 12pt}" +
                          "li{padding-left:2pt;margin:2pt 0}blockquote{margin:8pt 0 13pt;padding:8pt 12pt;background:#f2f6fb;border-left:3pt solid #91a9c5;color:#43546a;break-inside:avoid}" +
                          "blockquote p:last-child{margin-bottom:0}hr{border:0;border-top:1px solid #d9e0e8;margin:18pt 0}" +
                          "table{width:100%;border-collapse:collapse;margin:10pt 0 16pt;font-size:9.5pt;break-inside:auto}" +
                          "thead{display:table-header-group}tr{break-inside:avoid}th,td{border:1px solid #d5dee8;padding:6pt 8pt;text-align:left;vertical-align:top}" +
                          "th{background:#edf3f9;color:#243e5c;font-weight:600}tbody tr:nth-child(even){background:#f8fafc}" +
                          "code{font-family:Consolas,monospace;background:#f1f3f5;padding:1pt 3pt;border-radius:3pt;font-size:9pt}" +
                          "pre{white-space:pre-wrap;word-break:break-word;background:#f5f7fa;padding:10pt;border-radius:5pt;break-inside:avoid}" +
                          "a{color:#245b91;text-decoration:underline}" +
                          "</style></head><body><main class=\"document\">" + body + "</main></body></html>";
            var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs> handler =
                (_, args) => navigation.TrySetResult(args.IsSuccess);
            renderer.CoreWebView2.NavigationCompleted += handler;
            try
            {
                renderer.CoreWebView2.NavigateToString(html);
                if (!await navigation.Task.WaitAsync(TimeSpan.FromSeconds(30)))
                    throw new InvalidOperationException(LocalizationService.Current.Translate("pdf.layout_load_failed"));
                bool success = await renderer.CoreWebView2.PrintToPdfAsync(path, null);
                if (!success || !File.Exists(path)) throw new IOException(LocalizationService.Current.Translate("pdf.webview_generation_failed"));
            }
            finally { renderer.CoreWebView2.NavigationCompleted -= handler; }
        }
        finally { RenderGate.Release(); }
    }

    public static async Task ShowPreviewAsync(XamlRoot xamlRoot, string path, string title)
    {
        await PreviewGate.WaitAsync();
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            PdfDocument document = await PdfDocument.LoadFromFileAsync(file);
            var pages = new FlipView { MaxWidth = 900, MaxHeight = 660, HorizontalAlignment = HorizontalAlignment.Stretch };
            for (uint index = 0; index < document.PageCount; index++)
            {
                using PdfPage page = document.GetPage(index);
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream);
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                pages.Items.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 620 });
            }

            var description = new StackPanel { Spacing = 4 };
            description.Children.Add(new TextBlock
            {
                Text = LocalizationService.Current.Format("pdf.preview_description",
                    ("title", title), ("pages", document.PageCount)),
                FontSize = 12, Opacity = 0.72, TextWrapping = TextWrapping.Wrap
            });
            description.Children.Add(new TextBlock
            {
                Text = LocalizationService.Current.Format("pdf.saved_path", ("path", path)), FontSize = 11, Opacity = 0.64,
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
            });

            var content = new Grid { RowSpacing = 12 };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Children.Add(description);
            var openError = new InfoBar { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Error };
            Grid.SetRow(openError, 1);
            content.Children.Add(openError);
            var preview = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 32, 32, 32)),
                Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Child = pages
            };
            Grid.SetRow(preview, 2);
            content.Children.Add(preview);

            var footer = new Grid { ColumnSpacing = 8 };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var openButton = new Button
            {
                Content = LocalizationService.Current.Translate("pdf.open_with_default_app"), Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var closeButton = new Button
            {
                Content = LocalizationService.Current.Translate("common.close"), Padding = new Thickness(18, 6, 18, 6)
            };
            Grid.SetColumn(openButton, 1);
            Grid.SetColumn(closeButton, 2);
            footer.Children.Add(openButton);
            footer.Children.Add(closeButton);
            Grid.SetRow(footer, 3);
            content.Children.Add(footer);

            var dialog = new ContentDialog { Title = LocalizationService.Current.Translate("pdf.detailed_analysis_title"), Content = content, XamlRoot = xamlRoot };
            openButton.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    dialog.Hide();
                }
                catch (Exception ex) { InfoBarPresenter.Show(openError,
                    LocalizationService.Current.Translate("pdf.open_failed"), ex.Message, InfoBarSeverity.Error); }
            };
            closeButton.Click += (_, _) => dialog.Hide();
            dialog.KeyDown += (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Escape) dialog.Hide();
            };
            await ETSAssistant.WinUI3.Services.DiagnosticLogService.ShowDialogAsync(dialog);
        }
        finally { PreviewGate.Release(); }
    }

    public static void OpenWithDefaultProgram(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, nint token, out nint path);
}
