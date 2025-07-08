using System;
using System.Threading.Tasks;
using System.Timers;
using Velopack;
using System.Diagnostics;
using ProxChatClient.Models;

namespace ProxChatClient.Services;

public class UpdateService
{
    private readonly UpdateManager _updateManager;
    private readonly Config _config;
    private readonly System.Timers.Timer _updateTimer;
    private readonly DebugLogService _debugLog;
    private UpdateState _currentState = UpdateState.Idle;
    private UpdateInfo? _pendingUpdate = null;

    public event EventHandler<UpdateState>? UpdateStateChanged;
    public event EventHandler<int>? DownloadProgressChanged;

    public UpdateState CurrentState => _currentState;
    public UpdateInfo? PendingUpdate => _pendingUpdate;
    public string? PendingUpdateVersion => _pendingUpdate?.TargetFullRelease?.Version?.ToString();

    public UpdateService(Config config, DebugLogService debugLog)
    {
        _config = config;
        _debugLog = debugLog;
        _updateManager = new UpdateManager(_config.UpdateSettings.UpdateUrl);
        
        _debugLog.LogMain($"UpdateService initialized with URL: {_config.UpdateSettings.UpdateUrl}");
        _debugLog.LogMain($"Check for updates enabled: {_config.UpdateSettings.CheckForUpdates}");
        _debugLog.LogMain($"Check interval: {_config.UpdateSettings.CheckIntervalMinutes} minutes");
        
        // log current app version
        var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        _debugLog.LogMain($"Current app version: {currentVersion}");
        
        // setup periodic update checking
        _updateTimer = new System.Timers.Timer(_config.UpdateSettings.CheckIntervalMinutes * 60 * 1000); // convert to ms
        _updateTimer.Elapsed += async (s, e) => await CheckForUpdatesAsync();
        _updateTimer.AutoReset = true;
        
        // Don't start timer automatically - let MainViewModel start it after event subscriptions are complete
        _debugLog.LogMain("Update timer configured but not started yet - waiting for MainViewModel to start it");
    }

    public async Task CheckForUpdatesAsync()
    {
        if (!_config.UpdateSettings.CheckForUpdates || _currentState == UpdateState.Downloading)
        {
            _debugLog.LogMain($"Update check skipped - CheckForUpdates: {_config.UpdateSettings.CheckForUpdates}, CurrentState: {_currentState}");
            return;
        }

        _debugLog.LogMain($"Starting update check against URL: {_config.UpdateSettings.UpdateUrl}");

        try
        {
            SetState(UpdateState.Checking);
            _debugLog.LogMain("Update check started - calling Velopack UpdateManager.CheckForUpdatesAsync()");
            
            // check for new version
            var newVersion = await _updateManager.CheckForUpdatesAsync();
            if (newVersion != null)
            {
                _pendingUpdate = newVersion;
                var versionString = newVersion.TargetFullRelease?.Version?.ToString() ?? "Unknown";
                _debugLog.LogMain($"Update available! Version: {versionString}");
                SetState(UpdateState.Available);
                Trace.TraceInformation($"Update available: {versionString}");
            }
            else
            {
                _debugLog.LogMain("No updates available - application is up to date");
                SetState(UpdateState.UpToDate);
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogMain($"Update check FAILED with exception: {ex.GetType().Name}: {ex.Message}");
            _debugLog.LogMain($"Update check error details: {ex}");
            if (ex.InnerException != null)
            {
                _debugLog.LogMain($"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            Trace.TraceError($"Failed to check for updates: {ex.Message}");
            SetState(UpdateState.Error);
        }
    }

    public async Task DownloadUpdateAsync()
    {
        if (_pendingUpdate == null || _currentState != UpdateState.Available)
        {
            _debugLog.LogMain($"Update download skipped - PendingUpdate: {_pendingUpdate != null}, CurrentState: {_currentState}");
            return;
        }

        var versionString = _pendingUpdate.TargetFullRelease?.Version?.ToString() ?? "Unknown";
        _debugLog.LogMain($"Starting download of update version: {versionString}");

        try
        {
            SetState(UpdateState.Downloading);
            
            // download with progress reporting
            await _updateManager.DownloadUpdatesAsync(_pendingUpdate, OnDownloadProgress);
            
            _debugLog.LogMain($"Update download completed successfully: {versionString}");
            SetState(UpdateState.ReadyToApply);
            Trace.TraceInformation($"Update downloaded: {versionString}");
        }
        catch (Exception ex)
        {
            _debugLog.LogMain($"Update download FAILED: {ex.GetType().Name}: {ex.Message}");
            _debugLog.LogMain($"Download error details: {ex}");
            Trace.TraceError($"Failed to download update: {ex.Message}");
            SetState(UpdateState.Error);
        }
    }

    public void ApplyUpdateAndRestart()
    {
        if (_pendingUpdate != null && _currentState == UpdateState.ReadyToApply)
        {
            var versionString = _pendingUpdate.TargetFullRelease?.Version?.ToString() ?? "Unknown";
            _debugLog.LogMain($"Applying update and restarting application: {versionString}");
            Trace.TraceInformation($"Applying update: {versionString}");
            _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
        }
        else
        {
            _debugLog.LogMain($"Apply update skipped - PendingUpdate: {_pendingUpdate != null}, CurrentState: {_currentState}");
        }
    }

    private void OnDownloadProgress(int progressPercentage)
    {
        DownloadProgressChanged?.Invoke(this, progressPercentage);
    }

    private void SetState(UpdateState newState)
    {
        if (_currentState != newState)
        {
            var oldState = _currentState;
            _currentState = newState;
            _debugLog.LogMain($"Update state changed: {oldState} -> {newState}");
            
            _debugLog.LogMain($"SetState: About to enter try block for {newState}");
            try
            {
                _debugLog.LogMain($"SetState: Inside try block for {newState}");
                _debugLog.LogMain($"SetState: About to fire UpdateStateChanged event for {newState}");
                
                // Fire event asynchronously to prevent cross-thread deadlocks
                var eventToFire = UpdateStateChanged;
                if (eventToFire != null)
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            _debugLog.LogMain($"SetState: Firing UpdateStateChanged event async for {newState}");
                            eventToFire.Invoke(this, newState);
                            _debugLog.LogMain($"SetState: UpdateStateChanged event fired successfully for {newState}");
                        }
                        catch (Exception asyncEx)
                        {
                            _debugLog.LogMain($"SetState: Exception in async event firing: {asyncEx.Message}");
                        }
                    });
                }
                
                _debugLog.LogMain($"SetState: Event firing initiated for {newState}");
            }
            catch (Exception ex)
            {
                _debugLog.LogMain($"SetState: Exception firing UpdateStateChanged event: {ex.Message}");
                _debugLog.LogMain($"SetState: Exception stack trace: {ex.StackTrace}");
            }
            _debugLog.LogMain($"SetState: Completed for {newState}");
        }
    }

    public void StartAutomaticUpdateChecking()
    {
        if (_config.UpdateSettings.CheckForUpdates && _updateTimer != null)
        {
            _updateTimer.Start();
            _debugLog.LogMain("Automatic update checking started");
        }
    }

    public void Dispose()
    {
        _updateTimer?.Stop();
        _updateTimer?.Dispose();
    }
}

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToApply,
    Error
} 