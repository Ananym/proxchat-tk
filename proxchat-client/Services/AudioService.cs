using NAudio.Wave;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using ProxChatClient.Models;
using System.IO;
using NAudio.Utils; // For CircularBuffer
using NAudio.MediaFoundation; // For MP3 support
using OpusSharp.Core; // For OpusPredefinedValues

namespace ProxChatClient.Services;

public class AudioService : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveStream? _audioFileStream;

    private Timer? _audioFilePlaybackTimer;
    private string? _selectedInputDeviceName;
    private int _selectedInputDeviceNumber = -1;
    private readonly ConcurrentDictionary<string, PeerPlayback> _peerPlaybackStreams = new();
    // Track last received audio time for each peer to implement transmission indicators
    private readonly ConcurrentDictionary<string, DateTime> _lastAudioReceived = new();
    // Track current transmission state for each peer (separate from packet timing)
    private readonly ConcurrentDictionary<string, bool> _peerTransmissionStates = new();
    // Track last time each peer had meaningful audio (above threshold)
    private readonly ConcurrentDictionary<string, DateTime> _lastMeaningfulAudio = new();
    private readonly TimeSpan _transmissionTimeout = TimeSpan.FromMilliseconds(300); // Consider peer not transmitting after 300ms of silence
    private readonly TimeSpan _silenceWindow = TimeSpan.FromMilliseconds(200); // Keep indicator on for 200ms after silence
    private Timer? _transmissionCheckTimer;

    private readonly WaveFormat _captureFormat = new WaveFormat(48000, 16, 1); // Default microphone capture format
    
    private float _volumeScale = 0.5f;
    private float _inputVolumeScale = 1.0f;
    private float _minBroadcastThreshold = 0.0f;
    private readonly float _maxDistance;
    private bool _isSelfMuted;
    private bool _isPushToTalk;
    private bool _isPushToTalkActive;
    private readonly Config _config;
    private bool _useAudioFileInput;

    private string? _customAudioFilePath; // User-selected audio file path

    // Opus specific settings for WebRTC transmission
    internal const int DEFAULT_OPUS_SAMPLE_RATE = 48000; // Default Opus sample rate
    internal const int OPUS_CHANNELS = 1; // mono for voice chat
    internal const int OPUS_FRAME_SIZE_MS = 20; // Standard WebRTC packet size
    internal const int OPUS_MAX_PACKET_SIZE = 1276; // max opus packet size



    private readonly DebugLogService _debugLog;
    private OpusCodecService _opusCodec;
    private OpusCodecService? _fileOpusCodec; // separate codec for file input with different sample rate
    private readonly VolumeTransitionService _volumeTransitionService;

    // Buffer for microphone data before encoding
    private CircularBuffer? _microphoneCircularBuffer;

    
    // track last audio level update time to prevent stuck displays
    private DateTime _lastAudioLevelUpdate = DateTime.UtcNow;
    private Timer? _audioLevelResetTimer;

    // Add this field with the other private fields
    private int _audioFileCallbackCount = 0;
    private volatile bool _isDisposing = false; // Flag to prevent callbacks during disposal

    // Debug local playback for comparison
    private bool _debugLocalPlayback = false;
    private PeerPlayback? _debugLocalPlaybackStream;

    // Static constructor to initialize MediaFoundation for MP3 support
    static AudioService()
    {
        try
        {
            MediaFoundationApi.Startup();
        }
        catch (Exception ex)
        {
            // MediaFoundation might not be available on all systems
            Debug.WriteLine($"Warning: MediaFoundation initialization failed: {ex.Message}");
        }
    }

    public ObservableCollection<string> InputDevices { get; } = new();
    public event EventHandler<EncodedAudioPacketEventArgs>? EncodedAudioPacketAvailable;
    public event EventHandler<float>? AudioLevelChanged;
    public event EventHandler? RefreshedDevices;
    public event EventHandler<(string PeerId, bool IsTransmitting)>? PeerTransmissionChanged;

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

    public bool UseAudioFileInput
    {
        get => _useAudioFileInput;
        set
        {
            if (_useAudioFileInput != value)
            {
                StopCapture();
                _useAudioFileInput = value;
                
                try
                {
                    StartCapture();
                }
                catch (Exception ex)
                {
                    _debugLog.LogAudio($"Error starting capture: {ex.Message}");
                    throw;
                }
            }
        }
    }

    public string? CustomAudioFilePath
    {
        get => _customAudioFilePath;
        set
        {
            if (_customAudioFilePath != value)
            {
                _customAudioFilePath = value;
                
                // If using file input, restart capture to pick up the new file
                if (_useAudioFileInput)
                {
                    try
                    {
                        StopCapture();
                        StartCapture();
                    }
                    catch (Exception ex)
                    {
                        _debugLog.LogAudio($"Error restarting capture with new audio file: {ex.Message}");
                    }
                }
            }
        }
    }

    public string CurrentAudioFilePath => _customAudioFilePath ?? "test.wav";

    public float MinBroadcastThreshold
    {
        get => _minBroadcastThreshold;
        set
        {
            // validate input and clamp to safe range
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                _debugLog.LogAudio($"Invalid MinBroadcastThreshold: {value}. Using default value 0.0f");
                _minBroadcastThreshold = 0.0f;
            }
            else
            {
                _minBroadcastThreshold = Math.Clamp(value, 0.0f, 1.0f);
            }
            _debugLog.LogAudio($"MinBroadcastThreshold set to: {_minBroadcastThreshold}");
        }
    }

    public AudioService(DebugLogService debugLog, float maxDistance = 100.0f, Config? config = null)
    {
        _debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
        _maxDistance = maxDistance;
        _config = config ?? new Config();
        
        // Initialize Opus codec service with default 48kHz for microphone
        _opusCodec = new OpusCodecService(_debugLog, DEFAULT_OPUS_SAMPLE_RATE);
        
        // Initialize volume transition service
        _volumeTransitionService = new VolumeTransitionService(_debugLog);
        
        RefreshInputDevices();
        
        // Start timer to check for transmission timeouts
        _transmissionCheckTimer = new Timer(CheckTransmissionTimeouts, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        
        // Start timer to reset stuck audio level displays
        _audioLevelResetTimer = new Timer(CheckAudioLevelTimeout, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
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
             _debugLog.LogAudio($"Error refreshing input devices: {ex.Message}"); 
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
        if (_waveIn != null || _audioFilePlaybackTimer != null) 
        {
            return;
        }

        if (_useAudioFileInput)
        {
            string audioFilePath = CurrentAudioFilePath;
            if (!File.Exists(audioFilePath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioFilePath}");
            }
            StartAudioFileCapture();
        }
        else
        {
            StartMicrophoneCapture();
        }
    }

    private void StartAudioFileCapture()
    {
        try
        {
            string audioFilePath = CurrentAudioFilePath;
            
            if (!File.Exists(audioFilePath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioFilePath}");
            }

            _debugLog.LogAudio($"[FILE] Starting audio file capture: {audioFilePath}");

            // Determine file type and create appropriate reader
            string extension = Path.GetExtension(audioFilePath).ToLowerInvariant();
            WaveStream? reader = null;

            try
            {
                if (extension == ".mp3")
                {
                    reader = new MediaFoundationReader(audioFilePath);
                }
                else if (extension == ".wav")
                {
                    reader = new WaveFileReader(audioFilePath);
                }
                else
                {
                    try
                    {
                        reader = new MediaFoundationReader(audioFilePath);
                    }
                    catch
                    {
                        reader = new WaveFileReader(audioFilePath);
                    }
                }

                if (reader == null)
                {
                    throw new InvalidOperationException("Failed to create audio reader");
                }

                var waveFormat = reader.WaveFormat;
                _debugLog.LogAudio($"[FILE] Audio format: {waveFormat.Encoding}, {waveFormat.SampleRate}Hz, {waveFormat.Channels} channels");

                _audioFileStream = reader;

                // Check if file sample rate is supported by Opus, otherwise use 48kHz
                int fileSampleRate = (int)waveFormat.SampleRate;
                int[] supportedRates = { 8000, 12000, 16000, 24000, 48000 };
                int targetSampleRate = supportedRates.Contains(fileSampleRate) ? fileSampleRate : 48000;
                
                _fileOpusCodec?.Dispose(); // dispose any existing file codec
                
                try
                {
                    // use AUDIO mode for file input to preserve music quality instead of VOIP mode
                    _fileOpusCodec = new OpusCodecService(_debugLog, targetSampleRate, OpusPredefinedValues.OPUS_APPLICATION_AUDIO);
                    _debugLog.LogAudio($"[FILE] Created file-specific Opus codec: {fileSampleRate}Hz source -> {targetSampleRate}Hz Opus (AUDIO mode for music quality)");
                }
                catch (Exception codecEx)
                {
                    _debugLog.LogAudio($"[ERROR] Failed to create file-specific Opus codec: {codecEx.Message}");
                    reader?.Dispose();
                    throw new InvalidOperationException($"Failed to initialize Opus codec for audio file: {codecEx.Message}", codecEx);
                }

                // Calculate the correct timer interval based on the target codec frame size
                // This ensures we process exactly the right amount of audio for real-time playback
                double timerIntervalMs = (double)_fileOpusCodec.FrameSize / _fileOpusCodec.SampleRate * 1000.0;
                int timerInterval = (int)Math.Round(timerIntervalMs);
                
                _audioFilePlaybackTimer = new Timer(AudioFilePlaybackCallback, null, 0, timerInterval);
                _debugLog.LogAudio($"[FILE] Timer created with {timerInterval}ms interval (calculated from {_fileOpusCodec.FrameSize} samples at {_fileOpusCodec.SampleRate}Hz = {timerIntervalMs:F1}ms)");
            }
            catch (Exception ex)
            {
                reader?.Dispose();
                throw new InvalidDataException($"Failed to read audio file {audioFilePath}: {ex.Message}", ex);
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"[FILE] Error starting capture: {ex.Message}");
            _audioFileStream?.Dispose();
            _audioFileStream = null;
            _audioFilePlaybackTimer?.Dispose();
            _audioFilePlaybackTimer = null;
            throw;
        }
    }

    private void StartMicrophoneCapture()
    {
        if (_selectedInputDeviceNumber == -1)
        {
            throw new InvalidOperationException("No valid audio input device selected.");
        }

        try
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = _selectedInputDeviceNumber,
                WaveFormat = _captureFormat, // Capture at 48kHz, 16-bit
                BufferMilliseconds = OPUS_FRAME_SIZE_MS * 2 // Buffer slightly more to ensure enough data for processing
            };
            
            // Initialize circular buffer for microphone input (e.g., for 1 second of 48kHz, 16-bit mono audio)
            // calculate buffer size with validation to prevent extreme values
            int bufferSizeBytes = _captureFormat.SampleRate * _captureFormat.Channels * (_captureFormat.BitsPerSample / 8) * 1;
            
            // clamp buffer size to reasonable range (min 1KB, max 10MB)
            bufferSizeBytes = Math.Clamp(bufferSizeBytes, 1024, 10 * 1024 * 1024);
            
            _debugLog.LogAudio($"Initializing circular buffer with size: {bufferSizeBytes} bytes");
            _microphoneCircularBuffer = new CircularBuffer(bufferSizeBytes);


            _waveIn.DataAvailable += OnWaveInDataAvailable;
            _waveIn.RecordingStopped += (s, e) => { _debugLog.LogAudio("Microphone recording stopped."); };

            _waveIn.StartRecording();
            _debugLog.LogAudio($"Microphone capture started with format: {_captureFormat}, target packet duration: {OPUS_FRAME_SIZE_MS}ms");
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error starting audio capture: {ex.Message}");
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
            var silenceBuffer = new byte[0]; // Opus silence is empty packet
            EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silenceBuffer, 0));
            AudioLevelChanged?.Invoke(this, 0.0f); // Show 0 level when not transmitting due to mute/PTT
            return;
        }

        if (e.BytesRecorded == 0 || _microphoneCircularBuffer == null)
        {
            // ensure audio level updates even when no data is available
            try
            {
                AudioLevelChanged?.Invoke(this, 0.0f);
            }
            catch (Exception ex)
            {
                _debugLog.LogAudio($"Error firing AudioLevelChanged event for no data: {ex.Message}");
            }
            return;
        }

        // validate input data to prevent exceptions
        if (e.Buffer == null || e.BytesRecorded < 0 || e.BytesRecorded > e.Buffer.Length)
        {
            _debugLog.LogAudio($"Invalid audio data: BytesRecorded={e.BytesRecorded}, BufferLength={e.Buffer?.Length ?? 0}");
            try
            {
                AudioLevelChanged?.Invoke(this, 0.0f);
            }
            catch (Exception ex)
            {
                _debugLog.LogAudio($"Error firing AudioLevelChanged event for invalid data: {ex.Message}");
            }
            return;
        }

        // ensure we have even number of bytes for 16-bit samples
        int validBytesRecorded = e.BytesRecorded;
        if (validBytesRecorded % 2 != 0)
        {
            validBytesRecorded--; // drop the last odd byte to maintain 16-bit alignment
            _debugLog.LogAudio($"Adjusted BytesRecorded from {e.BytesRecorded} to {validBytesRecorded} for 16-bit alignment");
        }
        try
        {
            _microphoneCircularBuffer.Write(e.Buffer, 0, validBytesRecorded);
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error writing to circular buffer: {ex.Message}");
            // calculate audio level directly from incoming buffer as fallback
            try
            {
                float fallbackLevel = 0;
                // use validBytesRecorded and ensure we don't go out of bounds
                for (int i = 0; i < validBytesRecorded - 1; i += 2)
                {
                    if (i + 1 < e.Buffer.Length) // additional bounds check
                    {
                        short sample = BitConverter.ToInt16(e.Buffer, i);
                        // clamp sample to prevent overflow in calculations
                        sample = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
                        float normalizedSample = Math.Abs(sample) / 32768f;
                        // clamp normalized sample to valid range
                        normalizedSample = Math.Clamp(normalizedSample, 0.0f, 1.0f);
                        fallbackLevel = Math.Max(fallbackLevel, normalizedSample);
                    }
                }
                // clamp final level to valid range
                fallbackLevel = Math.Clamp(fallbackLevel, 0.0f, 1.0f);
                AudioLevelChanged?.Invoke(this, fallbackLevel);
                _lastAudioLevelUpdate = DateTime.UtcNow; // track successful fallback update
            }
            catch (Exception levelEx)
            {
                _debugLog.LogAudio($"Error calculating fallback audio level: {levelEx.Message}");
                AudioLevelChanged?.Invoke(this, 0.0f);
            }
            return; // exit early if buffer write fails
        }

        // Calculate required PCM bytes for one 20ms Opus frame
        // Use the actual frame size from the Opus codec (960 samples for 48kHz)
        
        // validate format parameters to prevent division by zero
        if (_opusCodec.SampleRate <= 0 || OPUS_FRAME_SIZE_MS <= 0 || _captureFormat.SampleRate <= 0 || _captureFormat.BitsPerSample <= 0)
        {
            _debugLog.LogAudio($"Invalid audio format parameters: OpusSampleRate={_opusCodec.SampleRate}, OPUS_FRAME_SIZE_MS={OPUS_FRAME_SIZE_MS}, CaptureRate={_captureFormat.SampleRate}, CaptureBits={_captureFormat.BitsPerSample}");
            return; // exit early to prevent calculation errors
        }
        
        int requiredInputBytes = _opusCodec.FrameSize * 2; // frame size * 2 bytes per sample
        
        // clamp to reasonable range to prevent extreme buffer requirements
        requiredInputBytes = Math.Clamp(requiredInputBytes, 32, 65536); // min 32 bytes, max 64KB per packet

        while (_microphoneCircularBuffer.Count >= requiredInputBytes)
        {
            var pcm48kHzBuffer = new byte[requiredInputBytes];
            
            // validate buffer size before reading
            if (requiredInputBytes <= 0 || requiredInputBytes > _microphoneCircularBuffer.Count)
            {
                _debugLog.LogAudio($"Invalid requiredInputBytes: {requiredInputBytes}, available: {_microphoneCircularBuffer.Count}");
                break; // exit the loop to prevent infinite processing
            }
            
            _microphoneCircularBuffer.Read(pcm48kHzBuffer, 0, requiredInputBytes);

            // Apply input volume scaling (on 48kHz PCM before encoding)
            if (_inputVolumeScale != 1.0f && pcm48kHzBuffer.Length >= 2)
            {
                int sampleCount = pcm48kHzBuffer.Length / 2;
                // validate sample count to prevent array issues
                if (sampleCount > 0 && sampleCount <= pcm48kHzBuffer.Length / 2)
                {
                    short[] samples = new short[sampleCount];
                    Buffer.BlockCopy(pcm48kHzBuffer, 0, samples, 0, pcm48kHzBuffer.Length);
                    
                    // clamp input volume scale to reasonable range to prevent extreme values
                    float clampedVolumeScale = Math.Clamp(_inputVolumeScale, 0.0f, 10.0f);
                    
                    for (int i = 0; i < sampleCount; i++)
                    {
                        // use double precision for intermediate calculation to prevent overflow
                        double scaledSample = samples[i] * clampedVolumeScale;
                        samples[i] = (short)Math.Clamp(scaledSample, short.MinValue, short.MaxValue);
                    }
                    Buffer.BlockCopy(samples, 0, pcm48kHzBuffer, 0, pcm48kHzBuffer.Length);
                }
            }
            
            // Calculate audio level for visualization from 48kHz data
            float maxSample = 0;
            try
            {
                // validate buffer length before processing
                if (pcm48kHzBuffer.Length >= 2)
                {
                    for (int i = 0; i < pcm48kHzBuffer.Length - 1; i += 2)
                    {
                        // additional bounds check to prevent out-of-range access
                        if (i + 1 < pcm48kHzBuffer.Length)
                        {
                            short sample = BitConverter.ToInt16(pcm48kHzBuffer, i);
                            // clamp sample to prevent overflow in abs calculation
                            sample = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
                            float normalizedSample = Math.Abs(sample) / 32768f;
                            // clamp normalized sample to valid range
                            normalizedSample = Math.Clamp(normalizedSample, 0.0f, 1.0f);
                            maxSample = Math.Max(maxSample, normalizedSample);
                        }
                    }
                }
                // clamp final max sample to valid range
                maxSample = Math.Clamp(maxSample, 0.0f, 1.0f);
            }
            catch (Exception ex)
            {
                _debugLog.LogAudio($"Error calculating audio level: {ex.Message}");
                maxSample = 0; // fallback to silence level on error
            }

            // Always show the actual detected audio level (color will indicate if broadcasting)
            // ensure this always fires even if encoding fails
            try
            {
                AudioLevelChanged?.Invoke(this, maxSample);
                _lastAudioLevelUpdate = DateTime.UtcNow; // track successful update
            }
            catch (Exception ex)
            {
                _debugLog.LogAudio($"Error firing AudioLevelChanged event: {ex.Message}");
            }

            // If audio level is below threshold, send silence but still show the level
            if (maxSample < _minBroadcastThreshold)
            {
                var silencePacket = new byte[0]; // Opus silence is empty packet
                try
                {
                    EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silencePacket, 0));
                }
                catch (Exception ex)
                {
                    _debugLog.LogAudio($"Error sending silence packet: {ex.Message}");
                }
                continue; // Process next chunk in buffer if any
            }

            try
            {
                // Convert PCM bytes to short array for Opus encoding
                short[] pcmSamples = OpusCodecService.BytesToShorts(pcm48kHzBuffer, pcm48kHzBuffer.Length);
                
                // Encode with Opus
                byte[] opusPacket = _opusCodec.Encode(pcmSamples, pcmSamples.Length);

                if (opusPacket.Length > 0)
                {
                    EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(opusPacket, opusPacket.Length));
                    
                    // Debug local playback: decode and play the same audio we're broadcasting
                    if (_debugLocalPlayback && _debugLocalPlaybackStream != null)
                    {
                        try
                        {
                            short[] decodedSamples = _opusCodec.Decode(opusPacket, opusPacket.Length);
                            if (decodedSamples.Length > 0)
                            {
                                byte[] monoPcmBuffer = OpusCodecService.ShortsToBytes(decodedSamples, decodedSamples.Length);
                                byte[] stereoPcmBuffer = ConvertMonoToStereoWithPanning(monoPcmBuffer, monoPcmBuffer.Length, 0.0f);
                                _debugLocalPlaybackStream.Buffer?.AddSamples(stereoPcmBuffer, 0, stereoPcmBuffer.Length);
                            }
                        }
                        catch (Exception debugEx)
                        {
                            _debugLog.LogAudio($"Error in debug local playback (microphone): {debugEx.Message}");
                        }
                    }
                }
                else
                {
                    _debugLog.LogAudio($"[WARNING] Opus encoding produced empty packet");
                    var silencePacket = new byte[0]; // Opus silence is empty packet
                    EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silencePacket, 0));
                }
            }
            catch (Exception ex)
            {
                _debugLog.LogAudio($"Error during microphone audio Opus encoding: {ex.Message}");
                // send silence packet on encoding error to maintain audio stream continuity
                try
                {
                    var silencePacket = new byte[0]; // Opus silence is empty packet
                    EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silencePacket, 0));
                }
                catch (Exception silenceEx)
                {
                    _debugLog.LogAudio($"Error sending silence packet after encoding failure: {silenceEx.Message}");
                }
            }
        }
    }

    private void AudioFilePlaybackCallback(object? state)
    {
        try
        {
            _audioFileCallbackCount++;
            _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} started");
            
            // Early exit if disposing to prevent null reference exceptions
            if (_isDisposing)
            {
                _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} exiting - service is disposing");
                return;
            }
            
            // Basic null checks first
            if (_debugLog == null || _opusCodec == null)
            {
                return;
            }
            
            // Critical: Ensure we have a valid file-specific codec before proceeding
            if (_fileOpusCodec == null)
            {
                _debugLog.LogAudio($"[ERROR] File-specific Opus codec is null in callback - this should not happen");
                var silenceBuffer = new byte[0];
                                    try { EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silenceBuffer, 0)); } catch (Exception eventEx) { _debugLog.LogAudio($"Error invoking EncodedAudioPacketAvailable (null codec): {eventEx.Message}"); }
                    try { AudioLevelChanged?.Invoke(this, 0.0f); } catch (Exception eventEx) { _debugLog.LogAudio($"Error invoking AudioLevelChanged (null codec): {eventEx.Message}"); }
                return;
            }
            
            _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} passed initial checks");

        _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} checking mute/stream state");
        
        if (_isSelfMuted || (_isPushToTalk && !_isPushToTalkActive) || _audioFileStream == null)
        {
            _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} sending silence - muted={_isSelfMuted}, ptt={_isPushToTalk && !_isPushToTalkActive}, nostream={_audioFileStream == null}");
            // Send silence when muted or push-to-talk is not active
            var silenceBuffer = new byte[0]; // Opus silence is empty packet
                                try { EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silenceBuffer, 0)); } catch (Exception eventEx) { _debugLog.LogAudio($"Error invoking EncodedAudioPacketAvailable (muted): {eventEx.Message}"); }
                    try { AudioLevelChanged?.Invoke(this, 0.0f); } catch (Exception eventEx) { _debugLog.LogAudio($"Error invoking AudioLevelChanged (muted): {eventEx.Message}"); }
            return;
        }

        _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} entering main processing");

        try
        {
            // Use the file-specific codec (we've verified it's not null above)
            var activeCodec = _fileOpusCodec;
            _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} got codec, creating PCM buffer");
            byte[] pcmData = new byte[activeCodec.FrameSize * 2]; // frame size * 2 bytes per sample
            float calculatedLevel = 0.0f;
            bool hasValidAudio = false;

            _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} processing audio file stream");
            // Process audio file stream
            var audioFileStreamRef = _audioFileStream; // Capture reference to avoid race condition
            if (audioFileStreamRef != null)
            {
                _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} getting source format");
                var sourceFormat = audioFileStreamRef.WaveFormat;
                if (sourceFormat == null) 
                {
                    _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} source format is null, returning");
                    return;
                }
                
                _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} source format: {sourceFormat.SampleRate}Hz, {sourceFormat.Channels}ch, {sourceFormat.BitsPerSample}bit");
                
                // Calculate how many bytes we need from the source for 20ms at the SOURCE sample rate
                // For 44.1kHz: 20ms = 44100 * 0.02 = 882 samples per channel
                double sourceSamplesFor20ms = sourceFormat.SampleRate * OPUS_FRAME_SIZE_MS / 1000.0;
                int sourceBytesNeeded = (int)(sourceSamplesFor20ms * sourceFormat.Channels * (sourceFormat.BitsPerSample / 8));
                
                // Ensure we have a reasonable buffer size and align to sample boundaries
                sourceBytesNeeded = Math.Max(1024, Math.Min(sourceBytesNeeded, 16384)); // between 1KB and 16KB
                // Align to sample boundary (ensure even number of bytes for 16-bit audio)
                if (sourceFormat.BitsPerSample == 16)
                {
                    sourceBytesNeeded = (sourceBytesNeeded / (sourceFormat.Channels * 2)) * (sourceFormat.Channels * 2);
                }
                
                _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} creating buffer for {sourceBytesNeeded} bytes");
                byte[] sourcePcmData = new byte[sourceBytesNeeded];
                
                _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} reading from audio stream");
                int bytesRead = audioFileStreamRef.Read(sourcePcmData, 0, sourceBytesNeeded);
                _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} read {bytesRead} bytes");
                
                if (bytesRead == 0) // End of file
                {
                    _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} end of file, restarting");
                    audioFileStreamRef.Position = 0; // Restart
                    bytesRead = audioFileStreamRef.Read(sourcePcmData, 0, sourceBytesNeeded);
                    _debugLog.LogAudio($"[FILE] Callback #{_audioFileCallbackCount} after restart, read {bytesRead} bytes");
                }
                
                if (bytesRead > 0)
                {
                    // Apply input volume scaling to source PCM data
                    ApplyInputVolumeScaling(sourcePcmData, bytesRead, sourceFormat);
                    
                    // Assume file is already 48kHz mono - use data directly
                    int targetBytes = activeCodec.FrameSize * 2; // target frame size in bytes
                    int bytesToUse = Math.Min(bytesRead, targetBytes);
                    
                    Array.Copy(sourcePcmData, pcmData, bytesToUse);
                    
                    // pad with silence if we didn't get enough data
                    if (bytesToUse < targetBytes)
                    {
                        Array.Fill(pcmData, (byte)0, bytesToUse, targetBytes - bytesToUse);
                    }
                    
                    // calculate audio level from the PCM data
                    calculatedLevel = CalculateLevelFromPcm(pcmData, bytesToUse);
                    hasValidAudio = bytesToUse > 0;
                }
                else
                {
                    // Empty file - fill with silence  
                    Array.Fill(pcmData, (byte)0);
                }
            }

            // Always show the actual detected audio level (color will indicate if broadcasting)
            if (!_isDisposing) // Don't invoke events if disposing
            {
                try 
                {
                    AudioLevelChanged?.Invoke(this, hasValidAudio ? calculatedLevel : 0.0f);
                }
                catch (Exception eventEx) 
                { 
                    _debugLog.LogAudio($"Error invoking AudioLevelChanged in file callback: {eventEx.Message}"); 
                }
            }

            // Apply broadcast threshold - if level is below threshold, send silence but still show the level
            if (hasValidAudio && calculatedLevel < _minBroadcastThreshold)
            {
                var silencePacket = new byte[0]; // Opus silence is empty packet
                if (!_isDisposing)
                {
                    try 
                    {
                        EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silencePacket, 0));
                    }
                    catch (Exception eventEx) 
                    { 
                        _debugLog.LogAudio($"Error invoking EncodedAudioPacketAvailable (threshold): {eventEx.Message}"); 
                    }
                }
                return;
            }

            // Encode with Opus using the file-specific codec
            if (hasValidAudio && pcmData.Length > 0) // Need some valid data
            {
                try
                {
                    // Validate PCM data before conversion to catch corruption early
                    bool isDataValid = true;
                    for (int i = 0; i < Math.Min(10, pcmData.Length - 1); i += 2)
                    {
                        try
                        {
                            short testSample = BitConverter.ToInt16(pcmData, i);
                            // If we can read a sample without exception, data is probably valid
                        }
                        catch
                        {
                            isDataValid = false;
                            break;
                        }
                    }
                    
                    if (!isDataValid)
                    {
                        _debugLog.LogAudio($"[ERROR] PCM data is corrupted, skipping encoding");
                        var silencePacket = new byte[0];
                        EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silencePacket, 0));
                        return;
                    }
                    
                    // Convert bytes to shorts and encode with the file-specific codec
                    int bytesToUse = Math.Min(pcmData.Length, activeCodec.FrameSize * 2); // use actual data size, not more than frame size
                    if (bytesToUse <= 0)
                    {
                        _debugLog.LogAudio($"[ERROR] No valid PCM data to encode: pcmData.Length={pcmData.Length}, frameSize={activeCodec.FrameSize}");
                        var silencePacket = new byte[0];
                EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silencePacket, 0));
                return;
            }

                    short[] pcmSamples = OpusCodecService.BytesToShorts(pcmData, bytesToUse);
                    

                    
                    // Validate sample count before encoding
                    if (pcmSamples.Length <= 0)
                    {
                        _debugLog.LogAudio($"[ERROR] Invalid sample count for Opus encoding: {pcmSamples.Length}");
                        var silencePacket = new byte[0];
                        EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silencePacket, 0));
                        return;
                    }
                    
                    byte[] opusPacket = activeCodec.Encode(pcmSamples, pcmSamples.Length);
                
                if (opusPacket.Length > 0)
                {
                    if (!_isDisposing)
                    {
                        try 
                        {
                            EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(opusPacket, opusPacket.Length));
                            
                            // Debug local playback: decode and play the same audio we're broadcasting
                            if (_debugLocalPlayback && _debugLocalPlaybackStream != null)
                            {
                                try
                                {
                                    // TEMPORARY: Skip Opus encode/decode and NAudio conversion for testing
                                    // Use original PCM data directly to isolate the source of harshness
                                    byte[] stereoPcmBuffer = ConvertMonoToStereoWithPanning(pcmData, pcmData.Length, 0.0f);
                                    _debugLocalPlaybackStream.Buffer?.AddSamples(stereoPcmBuffer, 0, stereoPcmBuffer.Length);
                                    
                                    // Original code (commented out for testing):
                                    // short[] decodedSamples = activeCodec.Decode(opusPacket, opusPacket.Length);
                                    // if (decodedSamples.Length > 0)
                                    // {
                                    //     byte[] monoPcmBuffer = OpusCodecService.ShortsToBytes(decodedSamples, decodedSamples.Length);
                                    //     byte[] stereoPcmBuffer = ConvertMonoToStereoWithPanning(monoPcmBuffer, monoPcmBuffer.Length, 0.0f);
                                    //     _debugLocalPlaybackStream.Buffer?.AddSamples(stereoPcmBuffer, 0, stereoPcmBuffer.Length);
                                    // }
                                }
                                catch (Exception debugEx)
                                {
                                    _debugLog.LogAudio($"Error in debug local playback: {debugEx.Message}");
                                }
                            }
                        }
                        catch (Exception eventEx) 
                        { 
                            _debugLog.LogAudio($"Error invoking EncodedAudioPacketAvailable (opus): {eventEx.Message}"); 
                        }
                    }
                    }
                    else
                    {
                        var silencePacket = new byte[0]; // Opus silence is empty packet
                        if (!_isDisposing)
                        {
                            try 
                            {
                                EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silencePacket, 0));
                            }
                            catch (Exception eventEx) 
                            { 
                                _debugLog.LogAudio($"Error invoking EncodedAudioPacketAvailable (silence): {eventEx.Message}"); 
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _debugLog.LogAudio($"[ERROR] Error encoding Opus from file audio: {ex.Message}");
                    var silencePacket = new byte[0]; // Opus silence is empty packet
                    if (!_isDisposing)
                    {
                        try 
                        {
                            EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silencePacket, 0));
                        }
                        catch (Exception eventEx) 
                        { 
                            _debugLog.LogAudio($"Error invoking EncodedAudioPacketAvailable (encode error): {eventEx.Message}"); 
                        }
                    }
                }
            }
            else
            {
                var silencePacket = new byte[0]; // Opus silence is empty packet
                if (!_isDisposing)
                {
                    try 
                    {
                        EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silencePacket, 0));
                    }
                    catch (Exception eventEx) 
                    { 
                        _debugLog.LogAudio($"Error invoking EncodedAudioPacketAvailable (no valid audio): {eventEx.Message}"); 
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"[ERROR] Error in audio file playback: {ex.Message}");
            // Send silence on error
            if (!_isDisposing)
            {
                var silenceBuffer = new byte[0]; // Opus silence is empty packet
                try { EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silenceBuffer, 0)); } catch (Exception eventEx) { _debugLog.LogAudio($"Error invoking EncodedAudioPacketAvailable: {eventEx.Message}"); }
                try { AudioLevelChanged?.Invoke(this, 0.0f); } catch (Exception eventEx) { _debugLog.LogAudio($"Error invoking AudioLevelChanged: {eventEx.Message}"); }
            }
        }
        }
        catch (Exception outerEx)
        {
            _debugLog.LogAudio($"[CRITICAL] Unhandled exception in AudioFilePlaybackCallback: {outerEx.Message}");
            _debugLog.LogAudio($"[CRITICAL] Stack trace: {outerEx.StackTrace}");
            // Try to send silence but don't let this crash either
            if (!_isDisposing)
            {
                try 
                { 
                    var silenceBuffer = new byte[0];
                    EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silenceBuffer, 0));
                    AudioLevelChanged?.Invoke(this, 0.0f);
                } 
                catch { }
            }
        }
    }

    public void StopCapture()
    {
        _audioFileCallbackCount = 0; // Reset callback counter
        
        if (_waveIn != null)
        {
            _waveIn.StopRecording();
            _waveIn.DataAvailable -= OnWaveInDataAvailable;
            _waveIn.Dispose();
            _waveIn = null;
        }

        if (_audioFilePlaybackTimer != null)
        {
            _audioFilePlaybackTimer.Dispose();
            _audioFilePlaybackTimer = null;
        }

        _audioFileStream?.Dispose();
            _audioFileStream = null;
        
        _fileOpusCodec?.Dispose();
        _fileOpusCodec = null;
    }

    public void PlayAudio(string peerId, byte[] data, int length)
    {
        if (!_peerPlaybackStreams.TryGetValue(peerId, out var playback))
        {
            // Peer playback not found, try to create it.
            _debugLog.LogAudio($"PlayAudio: PeerPlayback for peer {peerId} not found. Attempting to create.");
            playback = CreatePeerPlayback(peerId);
            if (playback == null)
            {
                _debugLog.LogAudio($"[ERROR] PlayAudio: Failed to create PeerPlayback for {peerId}. Audio data ignored.");
                return;
            }
            _debugLog.LogAudio($"PlayAudio: Successfully created PeerPlayback for {peerId} on-the-fly.");
        }

        if (playback == null || playback.WaveOut == null || playback.Buffer == null) 
        { 
            _debugLog.LogAudio($"[WARNING] PlayAudio: Peer {peerId} has null playback components (WaveOut or Buffer is null even after potential creation). Audio data ignored.");
            return; 
        }

        // Always update last received time for any packet (including silence)
        // This ensures the timeout mechanism works correctly
        var now = DateTime.UtcNow;
        _lastAudioReceived[peerId] = now;

        // Decode the Opus packet first to analyze the actual audio content
        bool hasAudio = false;
        short[] pcmSamples = new short[0];
        
        if (data.Length > 0)
        {
            try
            {
                // Decode Opus to PCM to analyze actual audio content
                pcmSamples = _opusCodec.Decode(data, length);
                
                if (pcmSamples.Length > 0)
                {
                    // Calculate audio level from decoded PCM to determine if there's meaningful audio
                    float audioLevel = CalculateAudioLevelFromSamples(pcmSamples);
                    
                    // Consider it meaningful audio if level is above a small threshold
                    const float TRANSMISSION_DETECTION_THRESHOLD = 0.001f; // Very low threshold for detection
                    hasAudio = audioLevel > TRANSMISSION_DETECTION_THRESHOLD;
                }
            }
            catch (Exception ex)
            {
                _debugLog.LogAudio($"Error decoding Opus for transmission detection from peer {peerId}: {ex.Message}");
                
                // If we get the specific "Negating the minimum value" error, it indicates corrupted packet data
                // This happens during reconnection when malformed packets arrive - just drop them
                if (ex.Message.Contains("Negating the minimum value"))
                {
                    _debugLog.LogAudio($"Detected corrupted Opus packet from peer {peerId} - dropping packet");
                    // Don't try to decode corrupted packets, just assume there's audio based on packet size
                    hasAudio = data.Length > 2;
                }
                else
                {
                    // Fall back to packet size check for other decode errors
                    hasAudio = data.Length > 2;
                }
            }
        }

        // Track transmission status with silence window logic
        var currentTime = DateTime.UtcNow;
        
        // If we have meaningful audio, update the last meaningful audio time
        if (hasAudio)
        {
            _lastMeaningfulAudio[peerId] = currentTime;
        }
        
        // Determine if we should show as transmitting based on silence window
        bool shouldShowTransmitting = false;
        if (hasAudio)
        {
            // Currently has audio - definitely transmitting
            shouldShowTransmitting = true;
        }
        else if (_lastMeaningfulAudio.TryGetValue(peerId, out var lastMeaningfulTime))
        {
            // no current audio, but check if we're still within the silence window
            shouldShowTransmitting = (currentTime - lastMeaningfulTime) < _silenceWindow;
        }
        
        // Check if transmission state has changed
        bool wasTransmitting = _peerTransmissionStates.GetValueOrDefault(peerId, false);
        if (shouldShowTransmitting != wasTransmitting)
        {
            _peerTransmissionStates[peerId] = shouldShowTransmitting;
            PeerTransmissionChanged?.Invoke(this, (peerId, shouldShowTransmitting));
        }

        if (!playback.IsMuted && hasAudio && pcmSamples.Length > 0)
        {
            try
            {
                // Convert short array to byte array
                byte[] monoPcmBuffer = OpusCodecService.ShortsToBytes(pcmSamples, pcmSamples.Length);
                
                // convert mono PCM to stereo PCM with panning
                byte[] stereoPcmBuffer = ConvertMonoToStereoWithPanning(monoPcmBuffer, monoPcmBuffer.Length, playback.PanningFactor);
                
                playback.Buffer.AddSamples(stereoPcmBuffer, 0, stereoPcmBuffer.Length);

            }
            catch (Exception ex)
            {
                _debugLog.LogAudio($"Error processing decoded Opus audio for peer {peerId}: {ex.Message}");
            }
        }
    }

    private PeerPlayback? CreatePeerPlayback(string peerId)
    {
        try
        {
            var waveOut = new WaveOutEvent();
            // create stereo format for panning support (8kHz, 16-bit, 2 channels)
            var stereoFormat = new WaveFormat(DEFAULT_OPUS_SAMPLE_RATE, 16, 2);
            var buffer = new BufferedWaveProvider(stereoFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(200)
            };
            
            waveOut.Init(buffer);
            waveOut.Play();

            var playback = new PeerPlayback 
            { 
                WaveOut = waveOut, 
                Buffer = buffer, 
                PeerId = peerId,
                PanningFactor = 0.0f, // start centered
                UiVolumeSetting = 0.0f // will be set by ViewModel immediately after creation
            };
            
            if (_peerPlaybackStreams.TryAdd(peerId, playback))
            {
                 _debugLog.LogAudio($"Created stereo audio playback for peer {peerId} with format {stereoFormat}. Volume will be set by ViewModel.");
                 return playback;
            }
            else
            {
                 _debugLog.LogAudio($"[CRITICAL] Failed to add peer playback to dictionary for {peerId} after creation.");
                 waveOut.Dispose(); // Clean up resources if add fails
                 return null;
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error creating audio playback for peer {peerId}: {ex.Message}");
            return null;
        }
    }

    public void RemovePeerAudioSource(string peerId)
    {
        if (_peerPlaybackStreams.TryRemove(peerId, out var playback))
        {
            try
            {
                playback.WaveOut?.Stop();
                playback.WaveOut?.Dispose();
                _debugLog.LogAudio($"Disposed audio playback for peer {peerId}");
            }
            catch (Exception ex)
            {
                _debugLog.LogAudio($"Error disposing audio playback for peer {peerId}: {ex.Message}");
            }
        }
        
        // Clean up volume transition tracking
        _volumeTransitionService.RemovePeer(peerId);
        
        // Clean up transmission tracking
        _lastAudioReceived.TryRemove(peerId, out _);
        _peerTransmissionStates.TryRemove(peerId, out _);
        _lastMeaningfulAudio.TryRemove(peerId, out _);
        
        // Force garbage collection to clean up any audio-related resources
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            _debugLog.LogAudio($"Forced GC after removing peer {peerId}");
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error during GC after peer removal: {ex.Message}");
        }
        
        // Notify that peer stopped transmitting
        try
        {
            PeerTransmissionChanged?.Invoke(this, (peerId, false));
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error invoking PeerTransmissionChanged for {peerId}: {ex.Message}");
        }
    }

    public void UpdatePeerDistance(string peerId, float distance)
    {
        if (_peerPlaybackStreams.TryGetValue(peerId, out var playback))
        {
            ApplyVolumeSettings(playback, distance);
        }
    }

    // new method that includes position data for stereo panning
    public void UpdatePeerPosition(string peerId, float distance, int myX, int myY, int peerX, int peerY)
    {
        if (_peerPlaybackStreams.TryGetValue(peerId, out var playback))
        {
            // calculate stereo panning based on X-axis offset
            float xOffset = peerX - myX;
            float panningFactor = CalculateStereoPanning(distance, xOffset);
            
            ApplyVolumeAndPanningSettings(playback, distance, panningFactor);
        }
    }

    private float CalculateStereoPanning(float distance, float xOffset)
    {
        float normalizedDistance = Math.Clamp(distance / _maxDistance, 0.0f, 1.0f);
        float panningStrength = normalizedDistance * normalizedDistance;
        // think squared is a bit more impactful than cubic here

        // determine direction: negative = left, positive = right
        float direction = Math.Sign(xOffset);
        
        // calculate final panning: 0 = center, -1 = full left, +1 = full right
        float panning = direction * panningStrength;
        
        return Math.Clamp(panning, -1.0f, 1.0f);
    }

    private float CalculateDistanceFactor(float distance)
    {
        // normalize distance to 0-1 range
        float normalizedDistance = Math.Clamp(distance / _maxDistance, 0.0f, 1.0f);
        
        // define our zones
        const float conversationZone = 0.2f; // first 20% of range has minimal falloff
        const float midZone = 0.5f; // 50% distance point
        const float exitZone = 0.8f; // 80% distance point
        
        float attenuationDB;
        if (normalizedDistance <= conversationZone)
        {
            // linear from 0dB to -3dB in conversation zone
            attenuationDB = -3.0f * (normalizedDistance / conversationZone);
        }
        else if (normalizedDistance <= midZone)
        {
            // linear from -3dB to -9dB between conversation and mid zone
            float progress = (normalizedDistance - conversationZone) / (midZone - conversationZone);
            attenuationDB = -3.0f + (-9.0f - (-3.0f)) * progress;
        }
        else if (normalizedDistance <= exitZone)
        {
            // linear from -9dB to -21dB between mid zone and exit zone
            float progress = (normalizedDistance - midZone) / (exitZone - midZone);
            attenuationDB = -9.0f + (-21.0f - (-9.0f)) * progress;
        } else
        {
            // linear from -21dB to -50dB from exit zone to max distance
            float progress = (normalizedDistance - exitZone) / (1.0f - exitZone);
            attenuationDB = -21.0f + (-50.0f - (-21.0f)) * progress;
        }
        
        // convert dB to linear
        float volumeScale = (float)Math.Pow(10.0, attenuationDB / 20.0);
        
        return Math.Clamp(volumeScale, 0.0f, 1.0f);
    }

    private float CalculateFinalVolume(PeerPlayback playback, float? distance = null)
    {
        if (playback == null) return 0.0f;

        if (playback.IsMuted) return 0.0f;

        float distanceFactor = 1.0f;
        if (distance.HasValue)
        {
            distanceFactor = CalculateDistanceFactor(distance.Value);
        }

        // calculate final volume as multiplication of all factors:
        // - distance factor (0-1, exponential falloff)
        // - peer UI volume setting (0-1, user adjustable per peer, default 0.5)
        // - overall volume scale (0-1, global setting, default 0.5)
        float finalVolume = distanceFactor * playback.UiVolumeSetting * _volumeScale;
        
        return Math.Clamp(finalVolume, 0.0f, 1.0f);
    }

    private void ApplyVolumeSettings(PeerPlayback playback, float? distance = null)
    {
        if (playback?.WaveOut == null) return;

        float finalVolume = CalculateFinalVolume(playback, distance);

        try
        {
            // use smooth volume transition instead of immediate change
            _volumeTransitionService.SetTargetVolume(playback.PeerId ?? "unknown", playback.WaveOut, finalVolume);
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error applying volume settings for peer {playback.PeerId}: {ex.Message}");
        }
    }

    private void ApplyVolumeAndPanningSettings(PeerPlayback playback, float distance, float panningFactor)
    {
        if (playback?.WaveOut == null) return;

        float finalVolume = CalculateFinalVolume(playback, distance);

        try
        {
            // use smooth volume transition instead of immediate change
            _volumeTransitionService.SetTargetVolume(playback.PeerId ?? "unknown", playback.WaveOut, finalVolume);
            
            // store panning factor for use during audio processing
            playback.PanningFactor = panningFactor;
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error applying volume/panning settings for peer {playback.PeerId}: {ex.Message}");
        }
    }

    public void SetPeerMuteState(string peerId, bool isMuted)
    {
        if (_peerPlaybackStreams.TryGetValue(peerId, out var playback))
        {
            if (playback.IsMuted != isMuted) // Only act if state is different
            {
                playback.IsMuted = isMuted;
                
                // for muting/unmuting, use immediate volume change for instant feedback
                if (isMuted)
                {
                    _volumeTransitionService.SetVolumeImmediate(peerId, playback.WaveOut, 0.0f);
                }
                else
                {
                    // when unmuting, apply normal volume settings with smooth transition
                    ApplyVolumeSettings(playback);
                }
            }
        }
    }

    public void TogglePeerMute(string peerId)
    {
        if (_peerPlaybackStreams.TryGetValue(peerId, out var playback))
        {
            playback.IsMuted = !playback.IsMuted;
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

    /// <summary>
    /// sets peer volume immediately without smooth transition
    /// useful for cases where instant feedback is needed
    /// </summary>
    public void SetPeerUiVolumeImmediate(string peerId, float uiVolume)
    {
        if (_peerPlaybackStreams.TryGetValue(peerId, out var playback))
        {
            playback.UiVolumeSetting = Math.Clamp(uiVolume, 0.0f, 1.0f);
            
            // We need to get the current distance to calculate the proper volume
            // Since we don't have distance here, we'll just call ApplyVolumeSettings
            // which will use the last known distance (or 1.0f if no distance set)
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
    }

    public void ToggleSelfMute()
    {
        SetSelfMuted(!_isSelfMuted);
    }

    public void SetPushToTalk(bool enabled)
    {
        _isPushToTalk = enabled;
        if (!enabled) _isPushToTalkActive = false; 
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
        // validate input and clamp to safe range
        if (float.IsNaN(scale) || float.IsInfinity(scale))
        {
            _debugLog.LogAudio($"Invalid input volume scale: {scale}. Using default value 1.0f");
            _inputVolumeScale = 1.0f;
        }
        else
        {
            _inputVolumeScale = Math.Clamp(scale, 0.0f, 5.0f);
        }
    }

    public void CreatePeerAudioStream(string peerId)
    {
        if (!_peerPlaybackStreams.ContainsKey(peerId))
        {
            CreatePeerPlayback(peerId);
        }
    }

    // new method to sync volume from ViewModel after peer creation
    public void SyncPeerVolumeFromViewModel(string peerId, float uiVolume)
    {
        if (_peerPlaybackStreams.TryGetValue(peerId, out var playback))
        {
            playback.UiVolumeSetting = Math.Clamp(uiVolume, 0.0f, 1.0f);
            // don't call ApplyVolumeSettings here as it will be called by distance updates
            _debugLog.LogAudio($"Synced volume for peer {peerId}: {uiVolume:F2}");
        }
    }

    public void SetCustomAudioFile(string? filePath)
    {
        CustomAudioFilePath = filePath;
    }

    public bool IsValidAudioFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return false;

        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        
        // Supported formats
        var supportedExtensions = new[] { ".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac" };
        
        return supportedExtensions.Contains(extension);
    }

    public void Dispose()
    {
        _isDisposing = true; // Set flag to prevent callbacks from running
        StopCapture();
        
        // Clean up debug local playback
        StopDebugLocalPlayback();

        foreach (var peerId in _peerPlaybackStreams.Keys.ToList())
        {
            RemovePeerAudioSource(peerId);
        }
        _peerPlaybackStreams.Clear();
        _microphoneCircularBuffer = null; // Release circular buffer
        _transmissionCheckTimer?.Dispose(); // Dispose transmission timer
        _audioLevelResetTimer?.Dispose(); // Dispose audio level reset timer
        _opusCodec?.Dispose(); // Dispose Opus codec service
        _fileOpusCodec?.Dispose(); // Dispose file-specific Opus codec service
        _volumeTransitionService?.Dispose(); // Dispose volume transition service
        GC.SuppressFinalize(this);
    }

    private void CheckTransmissionTimeouts(object? state)
    {
        var now = DateTime.UtcNow;
        var peersToUpdate = new List<string>();
        
        // Check for peers that have stopped sending packets entirely
        foreach (var kvp in _lastAudioReceived.ToList())
        {
            var peerId = kvp.Key;
            var lastReceived = kvp.Value;
            
            if ((now - lastReceived) > _transmissionTimeout)
            {
                // Peer hasn't sent any packets (including silence) for the timeout period
                // This means they're likely disconnected, so turn off transmission indicator
                if (_peerTransmissionStates.GetValueOrDefault(peerId, false))
                {
                    peersToUpdate.Add(peerId);
                }
                _lastAudioReceived.TryRemove(peerId, out _);
                _peerTransmissionStates.TryRemove(peerId, out _);
                _lastMeaningfulAudio.TryRemove(peerId, out _);
            }
        }
        
        // Check for peers whose silence window has expired
        foreach (var kvp in _lastMeaningfulAudio.ToList())
        {
            var peerId = kvp.Key;
            var lastMeaningfulTime = kvp.Value;
            
            // only check if peer is currently showing as transmitting
            if (_peerTransmissionStates.GetValueOrDefault(peerId, false))
            {
                if ((now - lastMeaningfulTime) >= _silenceWindow)
                {
                    // silence window has expired - turn off transmission indicator
                    peersToUpdate.Add(peerId);
                    _peerTransmissionStates[peerId] = false;
                }
            }
        }
        
        // Notify of transmission status changes for timed-out peers
        foreach (var peerId in peersToUpdate)
        {
            PeerTransmissionChanged?.Invoke(this, (peerId, false));
        }
    }

    private void CheckAudioLevelTimeout(object? state)
    {
        var now = DateTime.UtcNow;
        var timeSinceLastUpdate = now - _lastAudioLevelUpdate;
        
        // if no audio level update for more than 2 seconds, reset to 0
        if (timeSinceLastUpdate.TotalSeconds > 2.0 && _waveIn != null)
        {
            try
            {
                AudioLevelChanged?.Invoke(this, 0.0f);
                _lastAudioLevelUpdate = now; // prevent repeated resets
                _debugLog.LogAudio("Audio level display reset due to timeout (no updates for >2s)");
            }
            catch (Exception ex)
            {
                _debugLog.LogAudio($"Error resetting audio level on timeout: {ex.Message}");
            }
        }
    }

    // helper methods for audio level calculation and processing
    private void ApplyInputVolumeScaling(byte[] pcmData, int validBytes, WaveFormat format)
    {
        
        // only apply scaling if it's not 1.0 and we have valid data
        if (Math.Abs(_inputVolumeScale - 1.0f) < 0.001f || validBytes <= 0 || pcmData == null || format.BitsPerSample != 16)
        {
            return;
        }

        try
        {
            // clamp input volume scale to reasonable range
            float clampedVolumeScale = Math.Clamp(_inputVolumeScale, 0.0f, 10.0f);
            
            // ensure we have even number of bytes for 16-bit samples
            int alignedBytes = validBytes;
            if (alignedBytes % 2 != 0)
            {
                alignedBytes--; // drop the last odd byte
            }
            
            // apply scaling to 16-bit PCM samples
            for (int i = 0; i < alignedBytes - 1; i += 2)
            {
                if (i + 1 < pcmData.Length)
                {
                    short sample = BitConverter.ToInt16(pcmData, i);
                    // use double precision for intermediate calculation to prevent overflow
                    double scaledSample = sample * clampedVolumeScale;
                    sample = (short)Math.Clamp(scaledSample, short.MinValue, short.MaxValue);
                    
                    // write back the scaled sample
                    pcmData[i] = (byte)(sample & 0xFF);
                    pcmData[i + 1] = (byte)(sample >> 8);
                }
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error applying input volume scaling to audio file: {ex.Message}");
        }
    }



    private float CalculateLevelFromPcm(byte[] pcmData, int validBytes)
    {
        float maxSample = 0;
        
        // validate inputs to prevent exceptions
        if (pcmData == null || validBytes <= 0 || validBytes > pcmData.Length)
        {
            _debugLog.LogAudio($"Invalid PCM data for level calculation: data={pcmData != null}, validBytes={validBytes}, dataLength={pcmData?.Length ?? 0}");
            return 0.0f;
        }
        
        // ensure we have even number of bytes for 16-bit samples
        int alignedBytes = validBytes;
        if (alignedBytes % 2 != 0)
        {
            alignedBytes--; // drop the last odd byte
        }
        
        // analyze 16-bit PCM samples
        for (int i = 0; i < alignedBytes - 1; i += 2)
        {
            // additional bounds check
            if (i + 1 < pcmData.Length)
            {
                short sample = BitConverter.ToInt16(pcmData, i);
                // clamp sample to prevent overflow
                sample = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
                float normalizedSample = Math.Abs(sample) / 32768f;
                // clamp normalized sample to valid range
                normalizedSample = Math.Clamp(normalizedSample, 0.0f, 1.0f);
                maxSample = Math.Max(maxSample, normalizedSample);
            }
        }
        
        // clamp final result to valid range
        return Math.Clamp(maxSample, 0.0f, 1.0f);
    }





    private byte[] ConvertMonoToStereoWithPanning(byte[] monoPcmBuffer, int validBytes, float panningFactor)
    {
        // Convert mono PCM to stereo PCM with panning
        // validBytes is the number of bytes in the mono buffer
        int sampleCount = validBytes / 2; // 16-bit samples = 2 bytes per sample
        byte[] stereoPcmBuffer = new byte[validBytes * 2]; // stereo = double the bytes
        
        for (int i = 0; i < sampleCount; i++)
        {
            // read mono sample (16-bit)
            short monoSample = BitConverter.ToInt16(monoPcmBuffer, i * 2);
            
            // calculate left and right channel volumes based on panning
            // panningFactor: -1.0 = full left, 0.0 = center, +1.0 = full right
            float leftVolume = Math.Clamp(1.0f - panningFactor, 0.0f, 1.0f);
            float rightVolume = Math.Clamp(1.0f + panningFactor, 0.0f, 1.0f);
            
            short leftSample = (short)(monoSample * leftVolume);
            short rightSample = (short)(monoSample * rightVolume);
            
            // write stereo samples (left, then right)
            int stereoIndex = i * 4; // 4 bytes per stereo sample (2 channels * 2 bytes each)
            stereoPcmBuffer[stereoIndex] = (byte)(leftSample & 0xFF);
            stereoPcmBuffer[stereoIndex + 1] = (byte)(leftSample >> 8);
            stereoPcmBuffer[stereoIndex + 2] = (byte)(rightSample & 0xFF);
            stereoPcmBuffer[stereoIndex + 3] = (byte)(rightSample >> 8);
        }
        
        return stereoPcmBuffer;
    }

    private float CalculateAudioLevelFromSamples(short[] pcmSamples)
    {
        float maxSample = 0;
        
        // validate inputs to prevent exceptions
        if (pcmSamples == null || pcmSamples.Length <= 0)
        {
            return 0.0f;
        }
        
        // analyze 16-bit PCM samples
        for (int i = 0; i < pcmSamples.Length; i++)
        {
            float normalizedSample = Math.Abs(pcmSamples[i]) / 32768f;
            maxSample = Math.Max(maxSample, normalizedSample);
        }
        
        // clamp final result to valid range
        return Math.Clamp(maxSample, 0.0f, 1.0f);
    }

    public bool GetPeerTransmissionState(string peerId)
    {
        return _peerTransmissionStates.GetValueOrDefault(peerId, false);
    }

    public bool IsAudioInputReady()
    {
        if (_useAudioFileInput)
        {
            string audioFilePath = CurrentAudioFilePath;
            return !string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath);
        }
        else
        {
            return _selectedInputDeviceNumber != -1;
        }
    }

    public string GetAudioInputStatus()
    {
        if (_useAudioFileInput)
        {
            string audioFilePath = CurrentAudioFilePath;
            if (string.IsNullOrEmpty(audioFilePath))
            {
                return "No audio file selected";
            }
            else if (!File.Exists(audioFilePath))
            {
                return $"Audio file not found: {Path.GetFileName(audioFilePath)}";
            }
            else
            {
                return $"Audio file ready: {Path.GetFileName(audioFilePath)}";
            }
        }
        else
        {
            if (_selectedInputDeviceNumber == -1)
            {
                return "No microphone device selected";
            }
            else
            {
                return $"Microphone ready: {_selectedInputDeviceName}";
            }
        }
    }

    /// <summary>
    /// Debug feature: enable local playback of processed audio for comparison
    /// </summary>
    public bool DebugLocalPlayback
    {
        get => _debugLocalPlayback;
        set
        {
            if (_debugLocalPlayback != value)
            {
                _debugLocalPlayback = value;
                
                if (_debugLocalPlayback)
                {
                    StartDebugLocalPlayback();
                }
                else
                {
                    StopDebugLocalPlayback();
                }
            }
        }
    }

    private void StartDebugLocalPlayback()
    {
        try
        {
            if (_debugLocalPlaybackStream != null) return; // Already started
            
            _debugLocalPlaybackStream = CreatePeerPlayback("DEBUG_LOCAL");
            if (_debugLocalPlaybackStream != null)
            {
                _debugLocalPlaybackStream.UiVolumeSetting = 0.5f; // Set reasonable volume
                _debugLog.LogAudio("Started debug local playback - you should hear processed audio locally");
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error starting debug local playback: {ex.Message}");
        }
    }

    private void StopDebugLocalPlayback()
    {
        try
        {
            if (_debugLocalPlaybackStream != null)
            {
                _debugLocalPlaybackStream.WaveOut?.Stop();
                _debugLocalPlaybackStream.WaveOut?.Dispose();
                _debugLocalPlaybackStream = null;
                _debugLog.LogAudio("Stopped debug local playback");
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error stopping debug local playback: {ex.Message}");
        }
    }

    /// <summary>
    /// decode opus packet to pcm samples for use by webrtc aggregation
    /// </summary>
    /// <param name="opusPacket">encoded opus packet</param>
    /// <param name="packetLength">length of opus packet</param>
    /// <returns>decoded pcm samples</returns>
    public short[] DecodeOpusPacket(byte[] opusPacket, int packetLength)
    {
        try
        {
            return _opusCodec.Decode(opusPacket, packetLength);
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error decoding Opus packet for WebRTC aggregation: {ex.Message}");
            // return silence on decode error
            return new short[_opusCodec.FrameSize];
        }
    }
}

internal class PeerPlayback
{
    public string? PeerId { get; set; }
    public WaveOutEvent? WaveOut { get; set; }
    public BufferedWaveProvider? Buffer { get; set; }
    public bool IsMuted { get; set; }
    public float UiVolumeSetting { get; set; } // No default - must be set explicitly by ViewModel
    public float PanningFactor { get; set; } // for stereo panning
}

public class EncodedAudioPacketEventArgs : EventArgs
{
    public byte[] Buffer { get; }
    public int Samples { get; } // For PCMU, Samples == BytesRecorded (which is also packet size in bytes)

    public EncodedAudioPacketEventArgs(byte[] buffer, int samplesOrBytes)
    {
        this.Buffer = new byte[samplesOrBytes]; 
        System.Buffer.BlockCopy(buffer, 0, this.Buffer, 0, samplesOrBytes);
        Samples = samplesOrBytes;
    }
}

