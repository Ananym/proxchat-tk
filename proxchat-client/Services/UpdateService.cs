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
    private UpdateState _currentState = UpdateState.Idle;
    private UpdateInfo? _pendingUpdate = null;

    public event EventHandler<UpdateState>? UpdateStateChanged;
    public event EventHandler<int>? DownloadProgressChanged;

    public UpdateState CurrentState => _currentState;
    public UpdateInfo? PendingUpdate => _pendingUpdate;
    public string? PendingUpdateVersion => _pendingUpdate?.TargetFullRelease?.Version?.ToString();

    public UpdateService(Config config)
    {
        _config = config;
        _updateManager = new UpdateManager(_config.UpdateSettings.UpdateUrl);
        
        // setup periodic update checking
        _updateTimer = new System.Timers.Timer(_config.UpdateSettings.CheckIntervalMinutes * 60 * 1000); // convert to ms
        _updateTimer.Elapsed += async (s, e) => await CheckForUpdatesAsync();
        _updateTimer.AutoReset = true;
        
        if (_config.UpdateSettings.CheckForUpdates)
        {
            _updateTimer.Start();
        }
    }

    public async Task CheckForUpdatesAsync()
    {
        if (!_config.UpdateSettings.CheckForUpdates || _currentState == UpdateState.Downloading)
        {
            return;
        }

        try
        {
            SetState(UpdateState.Checking);
            
            // check for new version
            var newVersion = await _updateManager.CheckForUpdatesAsync();
            if (newVersion != null)
            {
                _pendingUpdate = newVersion;
                SetState(UpdateState.Available);
                Trace.TraceInformation($"Update available: {newVersion.TargetFullRelease?.Version}");
            }
            else
            {
                SetState(UpdateState.UpToDate);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to check for updates: {ex.Message}");
            SetState(UpdateState.Error);
        }
    }

    public async Task DownloadUpdateAsync()
    {
        if (_pendingUpdate == null || _currentState != UpdateState.Available)
        {
            return;
        }

        try
        {
            SetState(UpdateState.Downloading);
            
            // download with progress reporting
            await _updateManager.DownloadUpdatesAsync(_pendingUpdate, OnDownloadProgress);
            
            SetState(UpdateState.ReadyToApply);
            Trace.TraceInformation($"Update downloaded: {_pendingUpdate.TargetFullRelease?.Version}");
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to download update: {ex.Message}");
            SetState(UpdateState.Error);
        }
    }

    public void ApplyUpdateAndRestart()
    {
        if (_pendingUpdate != null && _currentState == UpdateState.ReadyToApply)
        {
            Trace.TraceInformation($"Applying update: {_pendingUpdate.TargetFullRelease?.Version}");
            _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
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
            _currentState = newState;
            UpdateStateChanged?.Invoke(this, newState);
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