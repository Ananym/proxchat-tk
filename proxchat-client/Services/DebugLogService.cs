using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace ProxChatClient.Services;

public class DebugLogService : IDisposable
{
    private StreamWriter? _logWriter;
    private readonly object _logLock = new object();
    private readonly string _logFilePath;
    private bool _disposed = false;
    
    // much more aggressive deduplication
    private readonly ConcurrentDictionary<string, DateTime> _lastLoggedTimes = new();
    private readonly TimeSpan _minLogInterval = TimeSpan.FromSeconds(10); // don't repeat same message for 10 seconds

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
            // clear existing log file on startup
            _logWriter = new StreamWriter(_logFilePath, append: false) { AutoFlush = true };
            Log($"=== Debug Log Started [{DateTime.Now}] ===");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize debug logging: {ex.Message}");
        }
    }

    private bool ShouldLog(string key)
    {
        var now = DateTime.UtcNow;
        if (_lastLoggedTimes.TryGetValue(key, out var lastTime))
        {
            if (now - lastTime < _minLogInterval)
            {
                return false; // too soon to log this again
            }
        }
        
        _lastLoggedTimes[key] = now;
        return true;
    }

    public void Log(string message, string? category = null)
    {
        if (_disposed || _logWriter == null) return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var categoryPrefix = !string.IsNullOrEmpty(category) ? $"[{category}] " : "";
        var logMessage = $"[{timestamp}] {categoryPrefix}{message}";
        
        lock (_logLock)
        {
            try
            {
                if (_logWriter == null)
                {
                    _logWriter = new StreamWriter(_logFilePath, append: true) { AutoFlush = true };
                }
                
                _logWriter.WriteLine(logMessage);
                _logWriter.Flush();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error writing to log file: {ex.Message}");
                try { _logWriter?.Dispose(); } catch { }
                _logWriter = null;
            }
        }
    }

    // log all WebRTC events
    public void LogWebRtc(string message, string? peerId = null)
            {
                Log(message, "WEBRTC");
    }

    // log all signaling events
    public void LogSignaling(string message, string? connectionId = null)
            {
                Log(message, "SIGNALING");
    }

    // log all audio events
    public void LogAudio(string message, string? deviceId = null)
            {
                Log(message, "AUDIO");
    }

    // main events - only log important state changes
    public void LogMain(string message)
    {
        // only log important main events
        if (message.Contains("character name change") ||
            message.Contains("connection") ||
            message.Contains("failed") ||
            message.Contains("ERROR") ||
            message.Contains("established") ||
            message.Contains("Auto-disconnecting") ||
            message.Contains("Debug") ||
            message.Contains("Adding peer") ||
            message.Contains("Distance calc") ||
            message.Contains("Peer") && message.Contains("transmission") ||
            message.Contains("OnGameDataRead") ||
            message.Contains("NamedPipe") ||
            message.Contains("GameData") ||
            message.Contains("MMF") ||
            message.Contains("memory") ||
            message.Contains("pipe") ||
            message.Contains("Success recovered") ||
            message.Contains("First failure"))
        {
            string key = $"MAIN_{message.Substring(0, Math.Min(40, message.Length))}";
            if (ShouldLog(key))
            {
                Log(message, "MAIN");
            }
        }
    }

    // method to reset deduplication for new connections
    public void ResetForNewConnection(string connectionId)
    {
        Log($"=== New Connection Session: {connectionId} ===", "SESSION");
    }

    // method to clear all deduplication (for major state changes)
    public void ClearDeduplication()
    {
        _lastLoggedTimes.Clear();
        Log("=== Deduplication Reset ===", "SESSION");
    }

    // add specific method for named pipe logging that's not filtered
    public void LogNamedPipe(string message)
    {
        Log(message, "NAMEDPIPE");
    }

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
