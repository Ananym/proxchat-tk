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
    private bool _hasUpdateAvailable = false;
    private UpdateInfo? _pendingUpdate = null;

    public event EventHandler<bool>? UpdateAvailabilityChanged;

    public bool HasUpdateAvailable => _hasUpdateAvailable;

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
        if (!_config.UpdateSettings.CheckForUpdates)
        {
            return;
        }

        try
        {
            // check for new version
            var newVersion = await _updateManager.CheckForUpdatesAsync();
            if (newVersion != null)
            {
                _pendingUpdate = newVersion;
                SetUpdateAvailable(true);
                
                // download in background
                await _updateManager.DownloadUpdatesAsync(_pendingUpdate);
                Trace.TraceInformation($"Update downloaded: {newVersion.TargetFullRelease?.Version}");
            }
            else
            {
                SetUpdateAvailable(false);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to check for updates: {ex.Message}");
            SetUpdateAvailable(false);
        }
    }

    public void ApplyUpdateAndRestart()
    {
        if (_pendingUpdate != null)
        {
            Trace.TraceInformation($"Applying update: {_pendingUpdate.TargetFullRelease?.Version}");
            _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
        }
    }

    private void SetUpdateAvailable(bool available)
    {
        if (_hasUpdateAvailable != available)
        {
            _hasUpdateAvailable = available;
            UpdateAvailabilityChanged?.Invoke(this, available);
        }
    }

    public void Dispose()
    {
        _updateTimer?.Stop();
        _updateTimer?.Dispose();
    }
} 