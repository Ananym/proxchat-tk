using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace ProxChatClient.Services;

public class DebugLogService : IDisposable
{
    private StreamWriter? _logWriter;
    private readonly object _logLock = new object();
    private readonly string? _logFilePath;
    private bool _disposed = false;
    
    // much more aggressive deduplication
    private readonly ConcurrentDictionary<string, DateTime> _lastLoggedTimes = new();
    private readonly TimeSpan _minLogInterval = TimeSpan.FromSeconds(10); // don't repeat same message for 10 seconds
    
    // category flags - readonly after construction for performance
    private readonly bool _audioEnabled;
    private readonly bool _webRtcEnabled;
    private readonly bool _signalingEnabled;
    private readonly bool _mainEnabled;
    private readonly bool _namedPipeEnabled;

    public DebugLogService(string? logFileName = null, bool enableAudio = false, bool enableWebRtc = true, bool enableSignaling = true, bool enableMain = true, bool enableNamedPipe = true)
    {
        // Get command line args to determine log file name if not provided
        if (string.IsNullOrEmpty(logFileName))
        {
            var args = Environment.GetCommandLineArgs();
            bool logRequested = false;
            
            // Look for log flag and optional filename
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--log" || args[i] == "-l")
                {
                    logRequested = true;
                    
                    // check if next arg exists and is not another flag
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                    {
                        logFileName = args[i + 1];
                    }
                    else
                    {
                        // --log flag present but no filename provided, default to "debug"
                        logFileName = "debug";
                    }
                    break;
                }
            }
            
            // if no --log argument found, don't create any log file
            if (!logRequested)
            {
                // set readonly category flags even when not logging
                _audioEnabled = enableAudio;
                _webRtcEnabled = enableWebRtc;
                _signalingEnabled = enableSignaling;
                _mainEnabled = enableMain;
                _namedPipeEnabled = enableNamedPipe;
                return;
            }
        }
        
        // use velopack-aware directory for log files
        _logFilePath = Path.Combine(ConfigService.GetConfigDirectory(), $"{logFileName}.log");
        
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
        
        // set readonly category flags
        _audioEnabled = enableAudio;
        _webRtcEnabled = enableWebRtc;
        _signalingEnabled = enableSignaling;
        _mainEnabled = enableMain;
        _namedPipeEnabled = enableNamedPipe;
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
        if (_disposed || _logWriter == null || _logFilePath == null) return;

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
        if (!_webRtcEnabled) return;
        Log(message, "WEBRTC");
    }

    // log all signaling events
    public void LogSignaling(string message, string? connectionId = null)
    {
        if (!_signalingEnabled) return;
        Log(message, "SIGNALING");
    }

    // log all audio events
    public void LogAudio(string message, string? deviceId = null)
    {
        if (!_audioEnabled) return;
        Log(message, "AUDIO");
    }

    // category control methods
    public bool IsCategoryEnabled(string category)
    {
        return category.ToUpperInvariant() switch
        {
            "AUDIO" => _audioEnabled,
            "WEBRTC" => _webRtcEnabled,
            "SIGNALING" => _signalingEnabled,
            "MAIN" => _mainEnabled,
            "NAMEDPIPE" => _namedPipeEnabled,
            _ => true // unknown categories default to enabled
        };
    }

    // main events
    public void LogMain(string message)
    {
        if (!_mainEnabled) return;
        string key = $"MAIN_{message.Substring(0, Math.Min(40, message.Length))}";
        if (ShouldLog(key))
        {
            Log(message, "MAIN");
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
        if (!_namedPipeEnabled) return;
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
