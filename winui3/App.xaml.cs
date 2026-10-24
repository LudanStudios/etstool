using System.Text;
using System.Threading;
using Microsoft.UI.Xaml;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3;

public partial class App : Application
{
    private static Mutex? _instanceMutex;
    private Window? _window;
    private static string ErrorLog => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ETS", "ets_winui_startup.log");

    public App()
    {
        UnhandledException += (_, args) => LogException(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogException(args.ExceptionObject as Exception);
        try { InitializeComponent(); }
        catch (Exception ex) { LogException(ex); throw; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _instanceMutex = new Mutex(true, "Local\\ETSAssistantWinUI3-v1", out bool createdNew);
            if (!createdNew) { Environment.Exit(0); return; }
            AliyunTtsCredentialStore.Initialize();
            _ = AppPreferencesService.Current.EnforceExperienceModeCredentialPolicy();
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex) { LogException(ex); throw; }
    }

    private static void LogException(Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog)!);
            File.AppendAllText(ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {exception}\n", Encoding.UTF8);
        }
        catch { }
    }
}
