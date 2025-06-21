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

        // initialize update service
        _updateService = new UpdateService(config);
        _updateService.UpdateAvailabilityChanged += OnUpdateAvailabilityChanged;

        // Get version from assembly
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionString = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v?.?.?";

        var mainWindow = new MainWindow
        {
            DataContext = _mainViewModel,
            Title = $"ProxChatTK {versionString}"
        };
        mainWindow.Show();

        // check for updates after window is shown
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000); // wait 2s before checking
            await _updateService.CheckForUpdatesAsync();
        });
    }

    private void OnUpdateAvailabilityChanged(object? sender, bool hasUpdate)
    {
        Dispatcher.Invoke(() =>
        {
            if (Application.Current.MainWindow is MainWindow mainWindow)
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                var versionString = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v?.?.?";
                
                if (hasUpdate)
                {
                    mainWindow.Title = $"ProxChatTK {versionString} - Update available, restart to apply";
                }
                else
                {
                    mainWindow.Title = $"ProxChatTK {versionString}";
                }
            }
        });
    }

    private Config LoadConfig()
    {
        try
        {
            // determine config path - for velopack, config is one level up from current/ folder
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string configPath;
            
            // check if we're running from a velopack 'current' folder
            if (Path.GetFileName(baseDir.TrimEnd(Path.DirectorySeparatorChar)) == "current")
            {
                // velopack structure: config.json is one level up from current/
                configPath = Path.Combine(Directory.GetParent(baseDir)!.FullName, "config.json");
            }
            else
            {
                // non-velopack or development: config.json is in the same folder as exe
                configPath = Path.Combine(baseDir, "config.json");
            }
            
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<Config>(json);
                if (config != null) return config;

                Trace.TraceWarning($"config.json found at {configPath} but could not be deserialized properly. Using default configuration.");
            }
            else
            {
                Trace.TraceWarning($"config.json not found at {configPath}. Using default configuration and creating a new one.");
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
            // save default config to the same location we tried to load from
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string newConfigPath;
            
            if (Path.GetFileName(baseDir.TrimEnd(Path.DirectorySeparatorChar)) == "current")
            {
                newConfigPath = Path.Combine(Directory.GetParent(baseDir)!.FullName, "config.json");
            }
            else
            {
                newConfigPath = Path.Combine(baseDir, "config.json");
            }
            
            string newJson = JsonConvert.SerializeObject(defaultConfig, Formatting.Indented);
            File.WriteAllText(newConfigPath, newJson);
            Trace.TraceInformation($"Created a new default config.json at {newConfigPath}.");
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
            // use same logic as config loading for log location
            string baseDir = AppContext.BaseDirectory;
            string logDir;
            
            if (Path.GetFileName(baseDir.TrimEnd(Path.DirectorySeparatorChar)) == "current")
            {
                // velopack: logs go to parent directory (persistent across updates)
                logDir = Directory.GetParent(baseDir)!.FullName;
            }
            else
            {
                // non-velopack: logs go to same directory as exe
                logDir = baseDir;
            }
            
            var logPath = Path.Combine(logDir, "fatal.log");
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

