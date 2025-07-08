using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using ProxChatClient.ViewModels;
using ProxChatClient.Models;
using ProxChatClient.Services;
using Newtonsoft.Json;
using Velopack;

namespace ProxChatClient;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private MainViewModel? _mainViewModel;
    private UpdateService? _updateService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // velopack startup - this must be first
        VelopackApp.Build().Run();

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        // Load configuration
        var configService = new ConfigService();
        Config config = configService.Config;

        // Check for debug flag before creating MainViewModel
#if DEBUG
        bool isDebugModeEnabled = e.Args.Contains("--debug");
#else
        // in release builds, debug mode is never available
        bool isDebugModeEnabled = false;
#endif

        // initialize debug log service (shared instance for all services)
        var debugLog = new DebugLogService();
        debugLog.LogMain("*** App.xaml.cs: Creating shared DebugLogService ***");

        // initialize update service
        debugLog.LogMain("*** App.xaml.cs: Creating UpdateService ***");
        _updateService = new UpdateService(config, debugLog);

        // Create MainViewModel with debug mode status, config service, update service, and shared debug log
        debugLog.LogMain("*** App.xaml.cs: Creating MainViewModel ***");
        _mainViewModel = new MainViewModel(configService, _updateService, debugLog, isDebugModeEnabled);

        // Get version from assembly
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionString = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v?.?.?";

        var mainWindow = new MainWindow
        {
            DataContext = _mainViewModel,
            Title = $"ProxChatTK {versionString}"
        };
        mainWindow.Show();

        // MainViewModel will handle initial update check after it finishes initializing
    }



    private void LogFatalException(Exception? ex, string source)
    {
        try
        {
            var logPath = Path.Combine(ConfigService.GetConfigDirectory(), "fatal.log");
            File.AppendAllText(logPath, $"[{DateTime.Now}] [{source}] {ex}\n");
        }
        catch { }
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Trace.TraceError($"AppDomain Unhandled Exception: {e.ExceptionObject}");
        LogFatalException(e.ExceptionObject as Exception, "CurrentDomain_UnhandledException");
        Shutdown(-1);
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Trace.TraceError($"TaskScheduler Unobserved Exception: {e.Exception}");
        LogFatalException(e.Exception, "UnobservedTaskException");
        e.SetObserved();
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Trace.TraceError($"Dispatcher Unhandled Exception: {e.Exception}");
        LogFatalException(e.Exception, "DispatcherUnhandledException");
        e.Handled = true;
        MessageBox.Show("An unexpected error occurred. Please check the log file for details.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

