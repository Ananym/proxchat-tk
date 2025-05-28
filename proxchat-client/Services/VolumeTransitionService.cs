using System.Collections.Concurrent;
using NAudio.Wave;

namespace ProxChatClient.Services;

/// <summary>
/// handles smooth volume transitions for audio playback streams
/// supports linear interpolation over configurable duration with resilience to rapid target changes
/// </summary>
public class VolumeTransitionService : IDisposable
{
    private readonly ConcurrentDictionary<string, VolumeTransition> _activeTransitions = new();
    private readonly Timer _updateTimer;
    private readonly DebugLogService _debugLog;
    private readonly object _lockObject = new object();
    
    // transition settings
    private const int TRANSITION_DURATION_MS = 200;
    private const int UPDATE_INTERVAL_MS = 10; // update every 10ms for smooth transitions
    private const float MIN_VOLUME_CHANGE = 0.001f; // minimum change to trigger transition
    
    public VolumeTransitionService(DebugLogService? debugLog = null)
    {
        _debugLog = debugLog ?? new DebugLogService();
        
        // start update timer for processing transitions
        _updateTimer = new Timer(UpdateTransitions, null, UPDATE_INTERVAL_MS, UPDATE_INTERVAL_MS);
    }
    
    /// <summary>
    /// sets target volume for a peer with smooth transition
    /// if peer already has active transition, updates target seamlessly
    /// </summary>
    public void SetTargetVolume(string peerId, WaveOutEvent? waveOut, float targetVolume)
    {
        if (waveOut == null) return;
        
        targetVolume = Math.Clamp(targetVolume, 0.0f, 1.0f);
        float currentVolume = waveOut.Volume;
        
        // if change is too small, apply immediately without transition
        if (Math.Abs(targetVolume - currentVolume) < MIN_VOLUME_CHANGE)
        {
            waveOut.Volume = targetVolume;
            // remove any existing transition since we're at target
            _activeTransitions.TryRemove(peerId, out _);
            return;
        }
        
        lock (_lockObject)
        {
            var now = DateTime.UtcNow;
            
            if (_activeTransitions.TryGetValue(peerId, out var existingTransition))
            {
                // update existing transition with new target
                // calculate current interpolated volume to use as new start point
                float currentInterpolatedVolume = CalculateCurrentVolume(existingTransition, now);
                
                existingTransition.StartVolume = currentInterpolatedVolume;
                existingTransition.TargetVolume = targetVolume;
                existingTransition.StartTime = now;
                existingTransition.WaveOut = waveOut; // update reference in case it changed
                
                _debugLog.LogAudio($"Updated volume transition for peer {peerId}: {currentInterpolatedVolume:F3} -> {targetVolume:F3}");
            }
            else
            {
                // create new transition
                var transition = new VolumeTransition
                {
                    PeerId = peerId,
                    WaveOut = waveOut,
                    StartVolume = currentVolume,
                    TargetVolume = targetVolume,
                    StartTime = now
                };
                
                _activeTransitions[peerId] = transition;
                _debugLog.LogAudio($"Started volume transition for peer {peerId}: {currentVolume:F3} -> {targetVolume:F3}");
            }
        }
    }
    
    /// <summary>
    /// immediately sets volume without transition and cancels any active transition
    /// </summary>
    public void SetVolumeImmediate(string peerId, WaveOutEvent? waveOut, float volume)
    {
        if (waveOut == null) return;
        
        volume = Math.Clamp(volume, 0.0f, 1.0f);
        waveOut.Volume = volume;
        
        // remove any active transition
        _activeTransitions.TryRemove(peerId, out _);
        
        _debugLog.LogAudio($"Set immediate volume for peer {peerId}: {volume:F3}");
    }
    
    /// <summary>
    /// removes peer from transition tracking (call when peer disconnects)
    /// </summary>
    public void RemovePeer(string peerId)
    {
        if (_activeTransitions.TryRemove(peerId, out _))
        {
            _debugLog.LogAudio($"Removed volume transition tracking for peer {peerId}");
        }
    }
    
    /// <summary>
    /// gets count of active transitions (for debugging/testing)
    /// </summary>
    public int GetActiveTransitionCount()
    {
        return _activeTransitions.Count;
    }
    
    /// <summary>
    /// checks if a peer has an active volume transition
    /// </summary>
    public bool HasActiveTransition(string peerId)
    {
        return _activeTransitions.ContainsKey(peerId);
    }
    
    private void UpdateTransitions(object? state)
    {
        var now = DateTime.UtcNow;
        var completedTransitions = new List<string>();
        
        lock (_lockObject)
        {
            foreach (var kvp in _activeTransitions)
            {
                var peerId = kvp.Key;
                var transition = kvp.Value;
                
                if (transition.WaveOut == null)
                {
                    completedTransitions.Add(peerId);
                    continue;
                }
                
                try
                {
                    float currentVolume = CalculateCurrentVolume(transition, now);
                    
                    // check if transition is complete
                    var elapsed = now - transition.StartTime;
                    if (elapsed.TotalMilliseconds >= TRANSITION_DURATION_MS)
                    {
                        // transition complete - set final volume and mark for removal
                        transition.WaveOut.Volume = transition.TargetVolume;
                        completedTransitions.Add(peerId);
                    }
                    else
                    {
                        // transition in progress - apply interpolated volume
                        transition.WaveOut.Volume = currentVolume;
                    }
                }
                catch (Exception ex)
                {
                    _debugLog.LogAudio($"Error updating volume transition for peer {peerId}: {ex.Message}");
                    completedTransitions.Add(peerId);
                }
            }
            
            // remove completed transitions
            foreach (var peerId in completedTransitions)
            {
                if (_activeTransitions.TryRemove(peerId, out var completedTransition))
                {
                    _debugLog.LogAudio($"Completed volume transition for peer {peerId} -> {completedTransition.TargetVolume:F3}");
                }
            }
        }
    }
    
    private static float CalculateCurrentVolume(VolumeTransition transition, DateTime now)
    {
        var elapsed = now - transition.StartTime;
        var progress = Math.Clamp(elapsed.TotalMilliseconds / TRANSITION_DURATION_MS, 0.0, 1.0);
        
        // linear interpolation
        return transition.StartVolume + (transition.TargetVolume - transition.StartVolume) * (float)progress;
    }
    
    public void Dispose()
    {
        _updateTimer?.Dispose();
        _activeTransitions.Clear();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// represents an active volume transition for a peer
/// </summary>
internal class VolumeTransition
{
    public string PeerId { get; set; } = string.Empty;
    public WaveOutEvent? WaveOut { get; set; }
    public float StartVolume { get; set; }
    public float TargetVolume { get; set; }
    public DateTime StartTime { get; set; }
} 