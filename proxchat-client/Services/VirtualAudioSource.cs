using SIPSorceryMedia.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ProxChatClient.Services;

/// <summary>
/// virtual audio source that aggregates audio from AudioService
/// maintains full 48kHz quality for music playback
/// </summary>
public class VirtualAudioSource : IAudioSource
{
    private readonly AudioService _audioService;
    private readonly DebugLogService _debugLog;
    private bool _isStarted = false;
    private bool _isPaused = false;
    private bool _disposed = false;

    private readonly List<AudioFormat> _supportedFormats;

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
#pragma warning disable CS0067 // event is required by interface but not used in this implementation
    public event RawAudioSampleDelegate? OnAudioSourceRawSample;
#pragma warning restore CS0067
    public event SourceErrorDelegate? OnAudioSourceError;

    public VirtualAudioSource(AudioService audioService, DebugLogService debugLog)
    {
        _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
        _debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));

        _debugLog.LogAudio("VirtualAudioSource constructor started");

        try
        {
            // use format ID 111 (common for Opus in WebRTC, within 96-127 dynamic range)
            _supportedFormats = new List<AudioFormat>
            {
                new AudioFormat(AudioCodecsEnum.OPUS, formatID: 111, clockRate: 48000, channelCount: 1, parameters: "opus")
            };

            _debugLog.LogAudio("VirtualAudioSource created with 48kHz Opus support");
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"ERROR in VirtualAudioSource constructor: {ex.Message}");
            _debugLog.LogAudio($"ERROR stack trace: {ex.StackTrace}");
            throw;
        }
    }

    public List<AudioFormat> GetAudioSourceFormats()
    {
        return _supportedFormats.ToList(); // return copy to prevent modification
    }

    public void SetAudioSourceFormat(AudioFormat audioFormat)
    {
        if (_supportedFormats.Any(f => f.Codec == audioFormat.Codec && f.ClockRate == audioFormat.ClockRate))
        {
            _debugLog.LogAudio($"VirtualAudioSource format set to: {audioFormat.Codec} @ {audioFormat.ClockRate}Hz");
        }
        else
        {
            _debugLog.LogAudio($"Warning: Unsupported audio format requested: {audioFormat.Codec} @ {audioFormat.ClockRate}Hz");
        }
    }

    public Task StartAudio()
    {
        if (_isStarted || _disposed) return Task.CompletedTask;

        try
        {
            _debugLog.LogAudio("VirtualAudioSource StartAudio() called");
            
            if (_audioService == null)
            {
                _debugLog.LogAudio("ERROR: _audioService is null in VirtualAudioSource.StartAudio()");
                throw new InvalidOperationException("AudioService is null");
            }
            
            _debugLog.LogAudio("About to subscribe to AudioService.EncodedAudioPacketAvailable");
            
            _audioService.EncodedAudioPacketAvailable += OnAudioPacketFromService;
            _isStarted = true;
            _isPaused = false;
            _debugLog.LogAudio("VirtualAudioSource started - connected to AudioService");
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error starting VirtualAudioSource: {ex.Message}");
            _debugLog.LogAudio($"Error stack trace: {ex.StackTrace}");
            throw;
        }

        return Task.CompletedTask;
    }

    public Task CloseAudio()
    {
        if (!_isStarted || _disposed) return Task.CompletedTask;

        try
        {
            _audioService.EncodedAudioPacketAvailable -= OnAudioPacketFromService;
            _isStarted = false;
            _isPaused = false;
            _debugLog.LogAudio("VirtualAudioSource stopped");
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error stopping VirtualAudioSource: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task PauseAudio()
    {
        _isPaused = true;
        _debugLog.LogAudio("VirtualAudioSource paused");
        return Task.CompletedTask;
    }

    public Task ResumeAudio()
    {
        _isPaused = false;
        _debugLog.LogAudio("VirtualAudioSource resumed");
        return Task.CompletedTask;
    }

    public bool IsAudioSourcePaused()
    {
        return _isPaused;
    }

    public bool HasEncodedAudioSubscribers()
    {
        return OnAudioSourceEncodedSample != null;
    }

    public void RestrictFormats(Func<AudioFormat, bool> filter)
    {
        // we always support Opus at 48kHz
        _debugLog.LogAudio("VirtualAudioSource: RestrictFormats called but not implemented (not needed)");
    }

    public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
    {
        // for our use case, we get encoded packets from AudioService, so this isn't used
        _debugLog.LogAudio("VirtualAudioSource: ExternalAudioSourceRawSample called but not implemented (not needed for our use case)");
    }

    private void OnAudioPacketFromService(object? sender, EncodedAudioPacketEventArgs e)
    {
        if (!_isStarted || _disposed || _isPaused) return;

        try
        {
            // forward the encoded audio packet directly to WebRTC
            // the packet is already Opus-encoded at 48kHz from AudioService
            if (e.Buffer != null && e.Buffer.Length > 0)
            {
                uint timestamp = (uint)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * 48); // 48kHz timestamp
                
                OnAudioSourceEncodedSample?.Invoke(timestamp, e.Buffer);
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error forwarding audio packet in VirtualAudioSource: {ex.Message}");
            OnAudioSourceError?.Invoke($"Error forwarding audio packet: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        CloseAudio().Wait();
        _disposed = true;
        _debugLog.LogAudio("VirtualAudioSource disposed");
    }
} 