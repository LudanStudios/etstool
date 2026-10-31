using System.Threading;
using Microsoft.UI.Xaml;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3;

public partial class App : Application
{
    private static Mutex? _instanceMutex;
    private static readonly List<Mutex> LegacyInstanceMutexes = [];
    private Window? _window;
    public App()
    {
        UnhandledException += (_, args) => DiagnosticLogService.Fatal("etstool.window.crashed", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                DiagnosticLogService.Fatal("etstool.process.crashed", exception);
            else
                DiagnosticLogService.Fatal("etstool.process.crashed", args.ExceptionObject?.ToString());
        };
        try { InitializeComponent(); }
        catch (Exception ex) { DiagnosticLogService.Fatal("etstool.startup.xaml_initialization.failed", ex); throw; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _ = AppPreferencesService.Current;
            DiagnosticLogService.Info("etstool.startup.started");
            _instanceMutex = new Mutex(true, "Local\\ETSAssistantWinUI3-v1", out bool createdNew);
            if (!createdNew)
            {
                DiagnosticLogService.Warn("etstool.startup.blocked", "reason=another_instance_running");
                Environment.Exit(0);
                return;
            }

            // Also reserve the former privilege-specific names so older builds
            // cannot run beside this single elevated instance.
            foreach (string legacyName in new[]
                     {
                         "Local\\ETSAssistantWinUI3-v1-Elevated",
                         "Local\\ETSAssistantWinUI3-v1-Standard"
                     })
            {
                var legacyMutex = new Mutex(true, legacyName, out bool legacyCreated);
                if (!legacyCreated)
                {
                    DiagnosticLogService.Warn("etstool.startup.blocked", "reason=legacy_instance_running");
                    Environment.Exit(0);
                    return;
                }
                LegacyInstanceMutexes.Add(legacyMutex);
            }

            AliyunTtsCredentialStore.Initialize();
            _window = new MainWindow();
            _window.Activate();
            DiagnosticLogService.Info("etstool.window.activation.requested");
            if (Environment.GetCommandLineArgs().Skip(1).Any(argument =>
                    string.Equals(argument, "--open-ets-preview", StringComparison.OrdinalIgnoreCase)))
                ((MainWindow)_window).NavigateToTag("etsPreview");
        }
        catch (Exception ex) { DiagnosticLogService.Fatal("etstool.startup.failed", ex); throw; }
    }
}
