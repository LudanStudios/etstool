using System.Text;
using System.Threading;
using Microsoft.UI.Xaml;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3;

public partial class App : Application
{
    private static Mutex? _instanceMutex;
    private static readonly List<Mutex> LegacyInstanceMutexes = [];
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

            // Also reserve the former privilege-specific names so older builds
            // cannot run beside this single elevated instance.
            foreach (string legacyName in new[]
                     {
                         "Local\\ETSAssistantWinUI3-v1-Elevated",
                         "Local\\ETSAssistantWinUI3-v1-Standard"
                     })
            {
                var legacyMutex = new Mutex(true, legacyName, out bool legacyCreated);
                if (!legacyCreated) { Environment.Exit(0); return; }
                LegacyInstanceMutexes.Add(legacyMutex);
            }

            AliyunTtsCredentialStore.Initialize();
            _window = new MainWindow();
            _window.Activate();
            if (Environment.GetCommandLineArgs().Skip(1).Any(argument =>
                    string.Equals(argument, "--open-ets-preview", StringComparison.OrdinalIgnoreCase)))
                ((MainWindow)_window).NavigateToTag("etsPreview");
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
