using NAudio.Wave;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using ProxChatClient.Models;
using System.IO;

namespace ProxChatClient.Services;

public class AudioService : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveStream? _audioFileStream;
    private Timer? _mp3PlaybackTimer;
    private string? _selectedInputDeviceName;
    private int _selectedInputDeviceNumber = -1;
    private readonly ConcurrentDictionary<string, PeerPlayback> _peerPlaybackStreams = new();
    private readonly WaveFormat _playbackFormat = new WaveFormat(48000, 16, 1);
    private float _volumeScale = 1.0f;
    private float _inputVolumeScale = 1.0f;
    private float _minBroadcastThreshold = 0.0f;
    private readonly float _maxDistance;
    private bool _isSelfMuted;
    private bool _isPushToTalk;
    private bool _isPushToTalkActive;
    private readonly Config _config;
    private bool _useMp3Input;
    private const string MP3_FILE_PATH = "test.mp3";
    private const int MP3_BUFFER_SIZE = 4800; // 100ms of audio at 48kHz

    public ObservableCollection<string> InputDevices { get; } = new();
    public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
    public event EventHandler<float>? AudioLevelChanged;
    public event EventHandler? RefreshedDevices;

    public string? SelectedInputDevice
    {
        get => _selectedInputDeviceName;
        set
        {
            if (_selectedInputDeviceName != value)
            {
                _selectedInputDeviceName = value;
                _selectedInputDeviceNumber = FindInputDeviceNumberByName(value);
                if (_waveIn != null)
                {
                    StopCapture();
                    StartCapture();
                }
                // Save to config when changed
                if (_config != null)
                {
                    _config.AudioSettings.SelectedInputDevice = value;
                }
            }
        }
    }

    public bool IsSelfMuted => _isSelfMuted;

    public bool UseMp3Input
    {
        get => _useMp3Input;
        set
        {
            if (_useMp3Input != value)
            {
                _useMp3Input = value;
                if (_waveIn != null)
                {
                    StopCapture();
                    StartCapture();
                }
            }
        }
    }

    public float MinBroadcastThreshold
    {
        get => _minBroadcastThreshold;
        set
        {
            _minBroadcastThreshold = Math.Clamp(value, 0.0f, 1.0f);
            Debug.WriteLine($"MinBroadcastThreshold set to: {_minBroadcastThreshold}");
        }
    }

    public AudioService(float maxDistance = 100.0f, Config? config = null)
    {
        _maxDistance = maxDistance;
        _config = config ?? new Config();
        RefreshInputDevices();
    }

    public void RefreshInputDevices()
    {
        InputDevices.Clear();
        try
        {
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var capabilities = WaveIn.GetCapabilities(i);
                InputDevices.Add(capabilities.ProductName);
            }
        }
        catch (Exception ex) 
        {
             Debug.WriteLine($"Error refreshing input devices: {ex.Message}"); 
        }

        // Try to restore the previously selected device from config
        var previouslySelected = _config?.AudioSettings.SelectedInputDevice;
        if (!string.IsNullOrEmpty(previouslySelected) && InputDevices.Contains(previouslySelected))
        {
            SelectedInputDevice = previouslySelected;
        }
        else
        {
            // If no saved device or it's not available, try to find a good default
            SelectedInputDevice = FindDefaultInputDevice();
        }
        
        RefreshedDevices?.Invoke(this, EventArgs.Empty);
    }

    private string? FindDefaultInputDevice()
    {
        // First try to find a device with "microphone" in the name
        var micDevice = InputDevices.FirstOrDefault(d => 
            d.Contains("microphone", StringComparison.OrdinalIgnoreCase) || 
            d.Contains("mic", StringComparison.OrdinalIgnoreCase));
        if (micDevice != null) return micDevice;

        // Then try to find a device with "input" in the name
        var inputDevice = InputDevices.FirstOrDefault(d => 
            d.Contains("input", StringComparison.OrdinalIgnoreCase));
        if (inputDevice != null) return inputDevice;

        // If no specific device found, just return the first available one
        return InputDevices.FirstOrDefault();
    }

    private int FindInputDeviceNumberByName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            if (WaveIn.GetCapabilities(i).ProductName == name)
            {
                return i;
            }
        }
        return -1;
    }

    public void StartCapture()
    {
        if (_waveIn != null || _mp3PlaybackTimer != null) 
        {
            Debug.WriteLine("Audio capture already started.");
            return;
        }

        if (_useMp3Input)
        {
            StartMp3Capture();
        }
        else
        {
            StartMicrophoneCapture();
        }
    }

    private void StartMp3Capture()
    {
        try
        {
            if (!File.Exists(MP3_FILE_PATH))
            {
                Debug.WriteLine($"MP3 file not found: {MP3_FILE_PATH}");
                throw new FileNotFoundException($"MP3 file not found: {MP3_FILE_PATH}");
            }

            _audioFileStream = new WaveFileReader(MP3_FILE_PATH);
            _mp3PlaybackTimer = new Timer(Mp3PlaybackCallback, null, 0, 100); // 100ms intervals
            Debug.WriteLine("Started MP3 playback capture");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error starting MP3 capture: {ex.Message}");
            _audioFileStream?.Dispose();
            _audioFileStream = null;
            _mp3PlaybackTimer?.Dispose();
            _mp3PlaybackTimer = null;
            throw;
        }
    }

    private void StartMicrophoneCapture()
    {
        if (_selectedInputDeviceNumber == -1)
        {
            Debug.WriteLine("Cannot start capture: No valid input device selected.");
            throw new InvalidOperationException("No valid audio input device selected.");
        }

        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = _selectedInputDeviceNumber,
                WaveFormat = _playbackFormat,
                BufferMilliseconds = 50
            };

            _waveIn.DataAvailable += OnWaveInDataAvailable;
            _waveIn.RecordingStopped += (s, e) => Debug.WriteLine("Audio capture stopped.");

            _waveIn.StartRecording();
            Debug.WriteLine($"Started audio capture on device {_selectedInputDeviceNumber}: {_selectedInputDeviceName}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error starting audio capture: {ex.Message}");
            _waveIn?.Dispose();
            _waveIn = null;
            throw;
        }
    }

    private void OnWaveInDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_isSelfMuted || (_isPushToTalk && !_isPushToTalkActive))
        {
            // Send silence when muted or push-to-talk is not active
            var silenceBuffer = new byte[e.BytesRecorded];
            AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(silenceBuffer, e.BytesRecorded));
            return;
        }

        // Calculate audio level for visualization and threshold check
        float maxSample = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i);
            float normalizedSample = Math.Abs(sample) / 32768f;
            maxSample = Math.Max(maxSample, normalizedSample);
        }
        AudioLevelChanged?.Invoke(this, maxSample);

        // If audio level is below threshold, send silence
        if (maxSample < _minBroadcastThreshold)
        {
            var silenceBuffer = new byte[e.BytesRecorded];
            AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(silenceBuffer, e.BytesRecorded));
            return;
        }

        // Apply input volume scaling
        if (_inputVolumeScale != 1.0f)
        {
            // Convert bytes to samples (16-bit audio = 2 bytes per sample)
            int sampleCount = e.BytesRecorded / 2;
            short[] samples = new short[sampleCount];
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);

            // Apply volume scaling to each sample
            for (int i = 0; i < sampleCount; i++)
            {
                samples[i] = (short)(samples[i] * _inputVolumeScale);
            }

            // Convert back to bytes
            byte[] scaledBuffer = new byte[e.BytesRecorded];
            Buffer.BlockCopy(samples, 0, scaledBuffer, 0, e.BytesRecorded);
            AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(scaledBuffer, e.BytesRecorded));
        }
        else
        {
            // No scaling needed, pass through original buffer
            AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(e.Buffer, e.BytesRecorded));
        }
    }

    private void Mp3PlaybackCallback(object? state)
    {
        if (_isSelfMuted || (_isPushToTalk && !_isPushToTalkActive) || _audioFileStream == null)
        {
            // Send silence when muted or push-to-talk is not active
            var silenceBuffer = new byte[MP3_BUFFER_SIZE];
            AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(silenceBuffer, MP3_BUFFER_SIZE));
            return;
        }

        try
        {
            byte[] buffer = new byte[MP3_BUFFER_SIZE];
            int bytesRead = _audioFileStream.Read(buffer, 0, buffer.Length);

            if (bytesRead == 0)
            {
                // End of file, restart
                _audioFileStream.Position = 0;
                bytesRead = _audioFileStream.Read(buffer, 0, buffer.Length);
            }

            if (bytesRead > 0)
            {
                // Calculate audio level for visualization and threshold check
                float maxSample = 0;
                for (int i = 0; i < bytesRead; i += 2)
                {
                    short sample = BitConverter.ToInt16(buffer, i);
                    float normalizedSample = Math.Abs(sample) / 32768f;
                    maxSample = Math.Max(maxSample, normalizedSample);
                }
                AudioLevelChanged?.Invoke(this, maxSample);

                // If audio level is below threshold, send silence
                if (maxSample < _minBroadcastThreshold)
                {
                    var silenceBuffer = new byte[bytesRead];
                    AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(silenceBuffer, bytesRead));
                    return;
                }

                // Apply input volume scaling
                if (_inputVolumeScale != 1.0f)
                {
                    int sampleCount = bytesRead / 2;
                    short[] samples = new short[sampleCount];
                    Buffer.BlockCopy(buffer, 0, samples, 0, bytesRead);

                    for (int i = 0; i < sampleCount; i++)
                    {
                        samples[i] = (short)(samples[i] * _inputVolumeScale);
                    }

                    byte[] scaledBuffer = new byte[bytesRead];
                    Buffer.BlockCopy(samples, 0, scaledBuffer, 0, bytesRead);
                    AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(scaledBuffer, bytesRead));
                }
                else
                {
                    AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(buffer, bytesRead));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in MP3 playback: {ex.Message}");
        }
    }

    public void StopCapture()
    {
        if (_waveIn != null)
        {
            Debug.WriteLine("Stopping microphone capture...");
            _waveIn.StopRecording();
            _waveIn.DataAvailable -= OnWaveInDataAvailable;
            _waveIn.Dispose();
            _waveIn = null;
        }

        if (_mp3PlaybackTimer != null)
        {
            Debug.WriteLine("Stopping MP3 playback...");
            _mp3PlaybackTimer.Dispose();
            _mp3PlaybackTimer = null;
        }

        if (_audioFileStream != null)
        {
            _audioFileStream.Dispose();
            _audioFileStream = null;
        }
    }

    public void PlayAudio(string peerId, byte[] data, int length)
    {
        if (!_peerPlaybackStreams.TryGetValue(peerId, out var playback))
        {
            // Don't create a playback stream if this is from an unknown peer
            // This can happen if audio data arrives before position data
            // The peer must first send its position to be considered connected
            Debug.WriteLine($"Received audio from unknown peer {peerId}. Ignoring until position data is received.");
            return;
        }

        if (!playback.IsMuted && playback.Buffer != null)
        {
             playback.Buffer.AddSamples(data, 0, length);
        }
    }

    private PeerPlayback? CreatePeerPlayback(string peerId)
    {
        try
        {
             Debug.WriteLine($"Creating playback stream for peer {peerId}");
            var waveOut = new WaveOutEvent();
            var buffer = new BufferedWaveProvider(_playbackFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(200)
            };
            waveOut.Init(buffer);
            waveOut.Play();

            var playback = new PeerPlayback { WaveOut = waveOut, Buffer = buffer, PeerId = peerId };
            if (_peerPlaybackStreams.TryAdd(peerId, playback))
            {
                 return playback;
            }
            else
            {
                 Debug.WriteLine($"Concurrency issue: Failed to add playback stream for {peerId}");
                 waveOut.Dispose();
                 return null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error creating WaveOutEvent for peer {peerId}: {ex.Message}");
            return null;
        }
    }

    public void RemovePeerAudioSource(string peerId)
    {
        if (_peerPlaybackStreams.TryRemove(peerId, out var playback))
        {
            Debug.WriteLine($"Removing playback stream for peer {peerId}");
            playback.WaveOut?.Stop();
            playback.WaveOut?.Dispose();
        }
    }

    public void UpdatePeerDistance(string peerId, float distance)
    {
        if (_peerPlaybackStreams.TryGetValue(peerId, out var playback))
        {
            ApplyVolumeSettings(playback, distance);
        }
    }

    private void ApplyVolumeSettings(PeerPlayback playback, float? distance = null)
    {
        if (playback?.WaveOut == null) return;

        float distanceFactor = 1.0f;
        if (distance.HasValue)
        {
            distanceFactor = Math.Clamp(1.0f - (distance.Value / _maxDistance), 0.0f, 1.0f);
        }
        else
        {
             Debug.WriteLine("ApplyVolumeSettings called without distance - volume might not reflect proximity.");
        }

        float finalVolume = playback.IsMuted 
                            ? 0.0f 
                            : distanceFactor * playback.UiVolumeSetting * _volumeScale;
        
        finalVolume = Math.Clamp(finalVolume, 0.0f, 1.0f);

        try
        {
            playback.WaveOut.Volume = finalVolume;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error applying volume settings for peer {playback.PeerId}: {ex.Message}");
        }
    }

    public void SetPeerMuteState(string peerId, bool isMuted)
    {
        if (_peerPlaybackStreams.TryGetValue(peerId, out var playback))
        {
            if (playback.IsMuted != isMuted) // Only act if state is different
            {
                playback.IsMuted = isMuted;
                Debug.WriteLine($"Peer {peerId} Mute State directly set to: {playback.IsMuted}");
                ApplyVolumeSettings(playback);
            }
        }
        else
        {
            Debug.WriteLine($"SetPeerMuteState: Could not find playback for peer {peerId}");
        }
    }

    public void TogglePeerMute(string peerId)
    {
        if (_peerPlaybackStreams.TryGetValue(peerId, out var playback))
        {
            playback.IsMuted = !playback.IsMuted;
            Debug.WriteLine($"Peer {peerId} Muted: {playback.IsMuted}");
            ApplyVolumeSettings(playback);
        }
    }

    public void SetPeerUiVolume(string peerId, float uiVolume)
    {
        if (_peerPlaybackStreams.TryGetValue(peerId, out var playback))
        {
            playback.UiVolumeSetting = Math.Clamp(uiVolume, 0.0f, 1.0f);
            ApplyVolumeSettings(playback);
        }
    }

    public void SetOverallVolumeScale(float scale)
    {
        _volumeScale = Math.Clamp(scale, 0.0f, 1.0f);
        foreach (var playback in _peerPlaybackStreams.Values)
        {
            ApplyVolumeSettings(playback);
        }
    }

    public void SetSelfMuted(bool muted)
    {
        _isSelfMuted = muted;
        Debug.WriteLine($"Self Muted: {_isSelfMuted}");
    }

    public void ToggleSelfMute()
    {
        SetSelfMuted(!_isSelfMuted);
    }

    public void SetPushToTalk(bool enabled)
    {
        _isPushToTalk = enabled;
        if (!enabled) _isPushToTalkActive = false; 
        Debug.WriteLine($"Push To Talk Enabled: {_isPushToTalk}");
    }

    public void SetPushToTalkActive(bool isActive)
    {
        if (_isPushToTalk)
        {
             _isPushToTalkActive = isActive;
        }
        else
        {
            _isPushToTalkActive = false;
        }
    }

    public void SetInputVolumeScale(float scale)
    {
        _inputVolumeScale = Math.Clamp(scale, 0.0f, 1.0f);
        Debug.WriteLine($"Input Volume Scale set to: {_inputVolumeScale}");
    }

    // Create a new public method to explicitly create the audio stream once position data is received
    public void CreatePeerAudioStream(string peerId)
    {
        if (!_peerPlaybackStreams.ContainsKey(peerId))
        {
            Debug.WriteLine($"Creating audio stream for peer {peerId} after position data received");
            CreatePeerPlayback(peerId);
        }
    }

    public void Dispose()
    {
        Debug.WriteLine("Disposing AudioService...");
        StopCapture();

        foreach (var peerId in _peerPlaybackStreams.Keys.ToList())
        {
            RemovePeerAudioSource(peerId);
        }
        _peerPlaybackStreams.Clear();
        GC.SuppressFinalize(this);
    }
}

internal class PeerPlayback
{
    public string? PeerId { get; set; }
    public WaveOutEvent? WaveOut { get; set; }
    public BufferedWaveProvider? Buffer { get; set; }
    public bool IsMuted { get; set; }
    public float UiVolumeSetting { get; set; } = 1.0f;
}

public class AudioDataEventArgs : EventArgs
{
    public byte[] Buffer { get; }
    public int BytesRecorded { get; }

    public AudioDataEventArgs(byte[] buffer, int bytesRecorded)
    {
        Buffer = new byte[bytesRecorded];
        System.Buffer.BlockCopy(buffer, 0, this.Buffer, 0, bytesRecorded);
        BytesRecorded = bytesRecorded;
    }
}