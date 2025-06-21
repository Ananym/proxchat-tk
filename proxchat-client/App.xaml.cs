using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ProxChatClient.ViewModels;
using ProxChatClient.Models;
using Newtonsoft.Json;

namespace ProxChatClient;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private MainViewModel? _mainViewModel;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        // Load configuration
        Config config = LoadConfig();

        // Check for debug flag before creating MainViewModel
#if DEBUG
        bool isDebugModeEnabled = e.Args.Contains("--debug");
#else
        // in release builds, debug mode is never available
        bool isDebugModeEnabled = false;
#endif

        // Create MainViewModel with debug mode status
        _mainViewModel = new MainViewModel(config, isDebugModeEnabled);

        var mainWindow = new MainWindow
        {
            DataContext = _mainViewModel
        };
        mainWindow.Show();
    }

    private Config LoadConfig()
    {
        try
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<Config>(json);
                if (config != null) return config;

                Trace.TraceWarning("config.json found but could not be deserialized properly. Using default configuration.");
            }
            else
            {
                Trace.TraceWarning("config.json not found. Using default configuration and creating a new one.");
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Error loading config.json: {ex.Message}. Using default configuration.");
        }
        
        // Create default config if not loaded or error occurred
        var defaultConfig = new Config();
        try 
        { 
            string newConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            string newJson = JsonConvert.SerializeObject(defaultConfig, Formatting.Indented);
            File.WriteAllText(newConfigPath, newJson);
            Trace.TraceInformation("Created a new default config.json.");
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to create default config.json: {ex.Message}");
        }
        return defaultConfig;
    }

    private void LogFatalException(Exception? ex, string source)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "fatal.log");
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

