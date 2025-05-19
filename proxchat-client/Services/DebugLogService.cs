using System;
using System.IO;
using System.Threading;

namespace ProxChatClient.Services;

public class DebugLogService : IDisposable
{
    private StreamWriter? _logWriter;
    private readonly object _logLock = new object();
    private readonly string _logFilePath;
    private bool _disposed = false;

    public DebugLogService(string? logFileName = null)
    {
        // Get command line args to determine log file name if not provided
        if (string.IsNullOrEmpty(logFileName))
        {
            var args = Environment.GetCommandLineArgs();
            logFileName = "debug";
            
            // Look for log filename argument
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--log" || args[i] == "-l")
                {
                    logFileName = args[i + 1];
                    break;
                }
            }
        }
        
        _logFilePath = Path.Combine(Directory.GetCurrentDirectory(), $"{logFileName}.log");
        
        try
        {
            _logWriter = new StreamWriter(_logFilePath, append: true) { AutoFlush = true };
            Log($"=== Debug Log Started [{DateTime.Now}] ===");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize debug logging: {ex.Message}");
        }
    }

    public void Log(string message, string? category = null)
    {
        if (_disposed || _logWriter == null) return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var threadId = Thread.CurrentThread.ManagedThreadId;
        var categoryPrefix = !string.IsNullOrEmpty(category) ? $"[{category}] " : "";
        var logMessage = $"[{timestamp}] [T{threadId}] {categoryPrefix}{message}";
        
        // Always write to debug output
        System.Diagnostics.Debug.WriteLine(logMessage);
        
        lock (_logLock)
        {
            try
            {
                if (_logWriter == null)
                {
                    // Try to reinitialize the writer if it's null
                    _logWriter = new StreamWriter(_logFilePath, append: true) { AutoFlush = true };
                }
                
                _logWriter.WriteLine(logMessage);
                _logWriter.Flush(); // Force immediate write
            }
            catch (Exception ex)
            {
                // Log the error to debug output
                System.Diagnostics.Debug.WriteLine($"Error writing to log file: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Log file path: {_logFilePath}");
                System.Diagnostics.Debug.WriteLine($"Failed to write message: {logMessage}");
                
                // Try to reinitialize the writer on next attempt
                try { _logWriter?.Dispose(); } catch { }
                _logWriter = null;
            }
        }
    }

    public void LogWebRtc(string message) => Log(message, "WEBRTC");
    public void LogSignaling(string message) => Log(message, "SIGNALING");
    public void LogAudio(string message) => Log(message, "AUDIO");
    public void LogMain(string message) => Log(message, "MAIN");

    public void Dispose()
    {
        if (_disposed) return;
        
        lock (_logLock)
        {
            Log("=== Debug Log Ended ===");
            _logWriter?.Dispose();
            _logWriter = null;
        }
        
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
