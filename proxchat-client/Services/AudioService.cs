using NAudio.Wave;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using ProxChatClient.Models;
using System.IO;
using NAudio.Utils; // For CircularBuffer

namespace ProxChatClient.Services;

public class AudioService : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveStream? _audioFileStream;
    private Stream? _rawMuLawStream;
    private Timer? _audioFilePlaybackTimer;
    private string? _selectedInputDeviceName;
    private int _selectedInputDeviceNumber = -1;
    private readonly ConcurrentDictionary<string, PeerPlayback> _peerPlaybackStreams = new();
    // Track last received audio time for each peer to implement transmission indicators
    private readonly ConcurrentDictionary<string, DateTime> _lastAudioReceived = new();
    private readonly TimeSpan _transmissionTimeout = TimeSpan.FromMilliseconds(500); // Consider peer not transmitting after 500ms of silence
    private Timer? _transmissionCheckTimer;
    // Playback format for incoming audio from peers (what WebRTC will give us, likely PCMU, then decoded)
    // For now, local playback will still use a higher quality format if possible, or what NAudio prefers.
    private readonly WaveFormat _playbackFormat = new WaveFormat(48000, 16, 1); // Output format for speakers
    private readonly WaveFormat _captureFormat = new WaveFormat(48000, 16, 1); // Default microphone capture format
    
    private float _volumeScale = 1.0f;
    private float _inputVolumeScale = 1.0f;
    private float _minBroadcastThreshold = 0.0f;
    private readonly float _maxDistance;
    private bool _isSelfMuted;
    private bool _isPushToTalk;
    private bool _isPushToTalkActive;
    private readonly Config _config;
    private bool _useAudioFileInput;
    private const string AUDIO_FILE_PATH = "test.wav"; // Should be MuLaw encoded for simplicity with _rawMuLawStream

    // PCMU/MuLaw specific settings for WebRTC transmission
    internal const int PCMU_SAMPLE_RATE = 8000;
    internal const int PCMU_CHANNELS = 1;
    private const int PCMU_BITS_PER_SAMPLE = 8;
    private const int TARGET_PACKET_DURATION_MS = 20; // Standard WebRTC packet size
    private const int PCMU_PACKET_SIZE_BYTES = (PCMU_SAMPLE_RATE * TARGET_PACKET_DURATION_MS / 1000) * (PCMU_BITS_PER_SAMPLE / 8) * PCMU_CHANNELS;

    private static readonly Random _random = new Random(); // For probabilistic logging
    private const double LOG_PROBABILITY = 0.01; // 1% chance to log detailed packet info

    private readonly DebugLogService _debugLog;

    // Buffer for microphone data before resampling and encoding
    private CircularBuffer? _microphoneCircularBuffer;
    private WaveFormat _pcm8KhzFormat = new WaveFormat(PCMU_SAMPLE_RATE, 16, PCMU_CHANNELS);


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
                _debugLog.LogAudio($"UseAudioFileInput changing from {_useAudioFileInput} to {value}");
                _useAudioFileInput = value;
                if (_waveIn != null || _audioFilePlaybackTimer != null)
                {
                    _debugLog.LogAudio("Stopping current capture before switching input type");
                    StopCapture();
                }
                _debugLog.LogAudio("Starting capture with new input type");
                StartCapture();
            }
        }
    }

    public float MinBroadcastThreshold
    {
        get => _minBroadcastThreshold;
        set
        {
            _minBroadcastThreshold = Math.Clamp(value, 0.0f, 1.0f);
            _debugLog.LogAudio($"MinBroadcastThreshold set to: {_minBroadcastThreshold}");
        }
    }

    public AudioService(float maxDistance = 100.0f, Config? config = null, DebugLogService? debugLog = null)
    {
        _maxDistance = maxDistance;
        _config = config ?? new Config();
        _debugLog = debugLog ?? new DebugLogService();
        RefreshInputDevices();
        
        // Start timer to check for transmission timeouts
        _transmissionCheckTimer = new Timer(CheckTransmissionTimeouts, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
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
        if (_waveIn != null || _audioFilePlaybackTimer != null) return;

        if (_useAudioFileInput)
        {
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
            string fullPath = Path.GetFullPath(AUDIO_FILE_PATH);
            
            if (!File.Exists(AUDIO_FILE_PATH))
            {
                throw new FileNotFoundException($"Audio file not found: {AUDIO_FILE_PATH}");
            }

            // open file and check header for MuLaw format
            var reader = new WaveFileReader(AUDIO_FILE_PATH);
            var waveFormat = reader.WaveFormat;

            if (waveFormat.Encoding == WaveFormatEncoding.MuLaw && waveFormat.SampleRate == PCMU_SAMPLE_RATE && waveFormat.Channels == PCMU_CHANNELS)
            {
                // use raw stream for MuLaw if it matches our target PCMU format
                _debugLog.LogAudio($"Using raw MuLaw stream for {AUDIO_FILE_PATH} as it matches target format.");
                _rawMuLawStream = File.OpenRead(AUDIO_FILE_PATH);
                // skip WAV header (usually 44 bytes for PCM, may vary for MuLaw files not strictly PCM wav)
                // A proper MuLaw .wav file will have a format chunk indicating MuLaw.
                // If it's a raw MuLaw file without a header, position should be 0.
                // For .wav files, NAudio's WaveFileReader.Position handles the data chunk start.
                // Let's assume WaveFileReader correctly positions us at the start of data if it's a valid WAV.
                // If we bypass WaveFileReader for MuLaw, we might need to handle headers manually or use raw .ulaw files.
                // For now, if WaveFileReader says it's MuLaw, we'll use its stream.
                _audioFileStream = reader; // Use the reader to benefit from its parsing.
                _rawMuLawStream = null; // Don't use the separate raw stream for now.
                                        // We'll read from _audioFileStream and it should give us MuLaw bytes.
            }
            else if (waveFormat.Encoding == WaveFormatEncoding.Pcm || waveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                // If it's PCM or Float, we'll need to convert it.
                _debugLog.LogAudio($"Audio file {AUDIO_FILE_PATH} is PCM/Float ({waveFormat}). Will convert to PCMU.");
                _audioFileStream = reader; // We'll read PCM and convert
                _rawMuLawStream = null;
            }
            else
            {
                reader.Dispose();
                throw new InvalidDataException($"Audio file {AUDIO_FILE_PATH} has unsupported format: {waveFormat.Encoding}. Only MuLaw or PCM are supported for file input.");
            }

            _audioFilePlaybackTimer = new Timer(AudioFilePlaybackCallback, null, 0, TARGET_PACKET_DURATION_MS);
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error starting audio file capture: {ex.Message}");
            _audioFileStream?.Dispose();
            _audioFileStream = null;
            _rawMuLawStream?.Dispose();
            _rawMuLawStream = null;
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
                BufferMilliseconds = TARGET_PACKET_DURATION_MS * 2 // Buffer slightly more to ensure enough data for processing
            };
            
            // Initialize circular buffer for microphone input (e.g., for 1 second of 48kHz, 16-bit mono audio)
            _microphoneCircularBuffer = new CircularBuffer(_captureFormat.SampleRate * _captureFormat.Channels * (_captureFormat.BitsPerSample / 8) * 1);


            _waveIn.DataAvailable += OnWaveInDataAvailable;
            _waveIn.RecordingStopped += (s, e) => { _debugLog.LogAudio("Microphone recording stopped."); };

            _waveIn.StartRecording();
            _debugLog.LogAudio($"Microphone capture started with format: {_captureFormat}, target packet duration: {TARGET_PACKET_DURATION_MS}ms");
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
            var silenceBuffer = new byte[PCMU_PACKET_SIZE_BYTES];
            Array.Fill(silenceBuffer, (byte)0xFF); // MuLaw silence
            EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silenceBuffer, silenceBuffer.Length));
            return;
        }

        if (e.BytesRecorded == 0 || _microphoneCircularBuffer == null)
        {
            return;
        }
        _microphoneCircularBuffer.Write(e.Buffer, 0, e.BytesRecorded);

        // Calculate required 48kHz PCM bytes for one 20ms 8kHz PCMU packet
        // One 20ms 8kHz PCMU packet = 160 samples.
        // To get 160 samples at 8kHz, we need 160 samples from the 8kHz resampled stream.
        // The resampler will take 48kHz data and output 8kHz. Ratio is 48/8 = 6.
        // So, we need 160 * 6 = 960 samples of 48kHz data.
        // Bytes for 960 samples of 48kHz, 16-bit mono = 960 * 2 bytes = 1920 bytes.
        int requiredInputBytes = (_captureFormat.SampleRate / PCMU_SAMPLE_RATE) * PCMU_PACKET_SIZE_BYTES * (_captureFormat.BitsPerSample / 8) / (PCMU_BITS_PER_SAMPLE / 8) ;


        while (_microphoneCircularBuffer.Count >= requiredInputBytes)
        {
            var pcm48kHzBuffer = new byte[requiredInputBytes];
            _microphoneCircularBuffer.Read(pcm48kHzBuffer, 0, requiredInputBytes);

            // Apply input volume scaling (on 48kHz PCM before resampling and encoding)
            if (_inputVolumeScale != 1.0f)
            {
                int sampleCount = pcm48kHzBuffer.Length / 2;
                short[] samples = new short[sampleCount];
                Buffer.BlockCopy(pcm48kHzBuffer, 0, samples, 0, pcm48kHzBuffer.Length);
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = (short)Math.Clamp(samples[i] * _inputVolumeScale, short.MinValue, short.MaxValue);
                }
                Buffer.BlockCopy(samples, 0, pcm48kHzBuffer, 0, pcm48kHzBuffer.Length);
            }
            
            // Calculate audio level for visualization from 48kHz data
            float maxSample = 0;
            for (int i = 0; i < pcm48kHzBuffer.Length; i += 2)
            {
                short sample = BitConverter.ToInt16(pcm48kHzBuffer, i);
                float normalizedSample = Math.Abs(sample) / 32768f;
                maxSample = Math.Max(maxSample, normalizedSample);
            }
            AudioLevelChanged?.Invoke(this, maxSample);

            // If audio level is below threshold, send silence
            if (maxSample < _minBroadcastThreshold)
            {
                var silencePacket = new byte[PCMU_PACKET_SIZE_BYTES];
                Array.Fill(silencePacket, (byte)0xFF); // MuLaw silence
                EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silencePacket, silencePacket.Length));
                continue; // Process next chunk in buffer if any
            }

            try
            {
                // Resample and encode
                using var rawSourceStream = new RawSourceWaveStream(pcm48kHzBuffer, 0, pcm48kHzBuffer.Length, _captureFormat);
                using var pcm8kHzStream = new WaveFormatConversionStream(_pcm8KhzFormat, rawSourceStream);
                
                // Encode PCM 8kHz 16-bit to MuLaw 8kHz 8-bit
                // WaveFormatConversionStream can convert to MuLaw directly
                var muLawFormat = WaveFormat.CreateMuLawFormat(PCMU_SAMPLE_RATE, PCMU_CHANNELS);
                using var muLawStream = new WaveFormatConversionStream(muLawFormat, pcm8kHzStream);

                byte[] pcmuPacket = new byte[PCMU_PACKET_SIZE_BYTES];
                int bytesRead = muLawStream.Read(pcmuPacket, 0, pcmuPacket.Length);

                if (bytesRead == PCMU_PACKET_SIZE_BYTES)
                {
                    EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(pcmuPacket, bytesRead));
                    if (_random.NextDouble() < LOG_PROBABILITY) // Probabilistic logging
                    {
                        _debugLog.LogAudio($"(Sampled Log) Mic: Sent {bytesRead} PCMU bytes after resample/encode.");
                    }
                }
                else if (bytesRead > 0)
                {
                    // Should ideally be PCMU_PACKET_SIZE_BYTES. If not, pad with silence or handle.
                     _debugLog.LogAudio($"[WARNING] Mic Resample/Encode: Produced {bytesRead} bytes, expected {PCMU_PACKET_SIZE_BYTES}. Padding with silence.");
                    var tempBuffer = new byte[PCMU_PACKET_SIZE_BYTES];
                    Array.Fill(tempBuffer, (byte)0xFF); // MuLaw silence
                    Buffer.BlockCopy(pcmuPacket, 0, tempBuffer, 0, bytesRead);
                    EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(tempBuffer, tempBuffer.Length));
                }
                 // else: not enough data produced, which shouldn't happen if requiredInputBytes is calculated correctly.
            }
            catch (Exception ex)
            {
                _debugLog.LogAudio($"Error during microphone audio resampling/encoding: {ex.Message}");
            }
        }
    }

    private void AudioFilePlaybackCallback(object? state)
    {
        if (_isSelfMuted || (_isPushToTalk && !_isPushToTalkActive) || (_audioFileStream == null && _rawMuLawStream == null))
        {
            // Send silence when muted or push-to-talk is not active
            var silenceBuffer = new byte[PCMU_PACKET_SIZE_BYTES];
            Array.Fill(silenceBuffer, (byte)0xFF); // MuLaw silence
            EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(silenceBuffer, silenceBuffer.Length));
            return;
        }

        try
        {
            // Favoring _audioFileStream which might be MuLaw or PCM from WaveFileReader
            if (_audioFileStream != null)
            {
                byte[] bufferToEncodeOrSend = new byte[0];
                int bytesToReadFromSource;

                if (_audioFileStream.WaveFormat.Encoding == WaveFormatEncoding.MuLaw && 
                    _audioFileStream.WaveFormat.SampleRate == PCMU_SAMPLE_RATE &&
                    _audioFileStream.WaveFormat.Channels == PCMU_CHANNELS)
                {
                    // Already in target MuLaw format
                    bytesToReadFromSource = PCMU_PACKET_SIZE_BYTES;
                    bufferToEncodeOrSend = new byte[bytesToReadFromSource];
                    int bytesRead = _audioFileStream.Read(bufferToEncodeOrSend, 0, bytesToReadFromSource);

                    if (bytesRead == 0) // End of file
                    {
                        _audioFileStream.Position = 0; // Restart
                        bytesRead = _audioFileStream.Read(bufferToEncodeOrSend, 0, bytesToReadFromSource);
                    }
                    if (bytesRead < bytesToReadFromSource && bytesRead > 0) // Partial read at EOF
                    {
                        var temp = new byte[bytesToReadFromSource];
                        Array.Fill(temp, (byte)0xFF); // MuLaw silence
                        Buffer.BlockCopy(bufferToEncodeOrSend, 0, temp, 0, bytesRead);
                        bufferToEncodeOrSend = temp;
                    }
                    else if (bytesRead == 0) // Still zero after trying to restart (e.g. empty file)
                    {
                         Array.Fill(bufferToEncodeOrSend, (byte)0xFF); // Send silence
                    }
                    // bufferToEncodeOrSend is now a full PCMU packet (or silence)
                }
                else // PCM or other format needing conversion
                {
                    // Calculate how much source PCM data we need to read to make one PCMU_PACKET_SIZE_BYTES packet
                    // Example: if source is 48kHz 16bit, and target is 8kHz 8bit MuLaw (160 bytes)
                    // We need 160 samples of 8kHz. Resample ratio 48/8 = 6.
                    // So, 160 * 6 = 960 samples of 48kHz. Bytes = 960 * 2 (16bit) = 1920 bytes from 48kHz source.
                    int sourceBytesPerSample = _audioFileStream.WaveFormat.BitsPerSample / 8;
                    
                    // This calculation is a bit simplified, ResampleRatio accounts for sample rate and channel differences.
                    // A more robust way is to read a fixed small amount of source, convert, and buffer the PCMU.
                    // For now, let's assume we read enough to make one packet.
                    // This needs a proper buffering and conversion pipeline like the microphone.
                    // For this iteration, if not MuLaw, it's complex. Let's log a TODO.
                     _debugLog.LogAudio($"[TODO] AudioFilePlaybackCallback: PCM/Other file format to PCMU conversion not fully implemented with robust buffering. File format: {_audioFileStream.WaveFormat}");
                    
                    // Fallback: send silence if file is not already in target MuLaw format for simplicity in this step
                    bufferToEncodeOrSend = new byte[PCMU_PACKET_SIZE_BYTES];
                    Array.Fill(bufferToEncodeOrSend, (byte)0xFF); // MuLaw silence
                }

                // For MuLaw, skip level calculation for now, or make it simpler
                AudioLevelChanged?.Invoke(this, 1.0f); // Max level for visualization of file audio
                EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(bufferToEncodeOrSend, bufferToEncodeOrSend.Length));
                if (_random.NextDouble() < LOG_PROBABILITY) // Probabilistic logging
                {
                    _debugLog.LogAudio($"(Sampled Log) File: Sent {bufferToEncodeOrSend.Length} bytes of audio data from file source (format: {_audioFileStream.WaveFormat.Encoding}).");
                }
            }
            // This rawMuLawStream path is now less likely due to StartAudioFileCapture changes.
            else if (_rawMuLawStream != null) 
            {
                // read raw MuLaw bytes
                byte[] buffer = new byte[PCMU_PACKET_SIZE_BYTES];
                int bytesRead = _rawMuLawStream.Read(buffer, 0, buffer.Length);
                
                if (bytesRead == 0)
                {
                    // end of file, restart
                    _rawMuLawStream.Position = 0; // Assuming raw file, no header
                    bytesRead = _rawMuLawStream.Read(buffer, 0, buffer.Length);
                }

                if (bytesRead < PCMU_PACKET_SIZE_BYTES && bytesRead > 0) //EOF, partial read
                {
                    var tempBuffer = new byte[PCMU_PACKET_SIZE_BYTES];
                    Array.Fill(tempBuffer, (byte)0xFF); // MuLaw silence
                    Buffer.BlockCopy(buffer, 0, tempBuffer, 0, bytesRead);
                    buffer = tempBuffer;
                    bytesRead = buffer.Length;
                }
                else if (bytesRead == 0) // Still zero after restart
                {
                     Array.Fill(buffer, (byte)0xFF); // Send silence
                     bytesRead = buffer.Length;
                }
                
                if (bytesRead > 0)
                {
                    AudioLevelChanged?.Invoke(this, 1.0f); // Max for visualization
                    EncodedAudioPacketAvailable?.Invoke(this, new EncodedAudioPacketEventArgs(buffer, bytesRead));
                    if (_random.NextDouble() < LOG_PROBABILITY) // Probabilistic logging
                    {
                        _debugLog.LogAudio($"(Sampled Log) File (raw): Sent {bytesRead} bytes of MuLaw audio data from raw file.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error in audio playback: {ex.Message}");
        }
    }

    public void StopCapture()
    {
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

        if (_audioFileStream != null)
        {
            _audioFileStream.Dispose();
            _audioFileStream = null;
        }
        if (_rawMuLawStream != null)
        {
            _rawMuLawStream.Dispose();
            _rawMuLawStream = null;
        }
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

        // Check if this is actual audio data (not silence)
        bool hasAudio = false;
        if (data.Length > 0)
        {
            // For MuLaw (PCMU), 0xFF is silence, so check if any byte is different
            hasAudio = data.Any(b => b != 0xFF);
        }

        // Track transmission status
        if (hasAudio)
        {
            var now = DateTime.UtcNow;
            bool wasTransmitting = _lastAudioReceived.ContainsKey(peerId);
            _lastAudioReceived[peerId] = now;
            
            // If peer wasn't transmitting before, notify of transmission start
            if (!wasTransmitting)
            {
                PeerTransmissionChanged?.Invoke(this, (peerId, true));
            }
        }

        if (!playback.IsMuted)
        {
            try
            {
                // Incoming data is PCMU (MuLaw) 8kHz, 8-bit, 1 channel.
                // The playback.Buffer is (now) configured for 8kHz, 16-bit PCM, 1 channel.
                // We need to decode MuLaw to PCM.
                using var ms = new MemoryStream(data, 0, length);
                using var muLawReader = new MuLawWaveStream(ms); // Helper stream to read MuLaw
                using var pcmStream = new WaveFormatConversionStream(_pcm8KhzFormat, muLawReader);
                
                byte[] pcmBuffer = new byte[length * 2]; // PCM 16-bit will be twice the size of MuLaw 8-bit
                int bytesDecoded = pcmStream.Read(pcmBuffer, 0, pcmBuffer.Length);

                if (bytesDecoded > 0)
                {
                    playback.Buffer.AddSamples(pcmBuffer, 0, bytesDecoded);
                    _debugLog.LogAudio($"Played {bytesDecoded} decoded PCM bytes from peer {peerId} (original MuLaw: {length} bytes).");
                }
            }
            catch (Exception ex)
            {
                _debugLog.LogAudio($"Error decoding/playing audio for peer {peerId}: {ex.Message}");
            }
        }
    }

    private PeerPlayback? CreatePeerPlayback(string peerId)
    {
        try
        {
            var waveOut = new WaveOutEvent();
            // The BufferedWaveProvider should be configured for the format we will ADD to it,
            // which is 8kHz, 16-bit PCM (after decoding from MuLaw).
            var buffer = new BufferedWaveProvider(_pcm8KhzFormat) // Use _pcm8KhzFormat
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(200) // Increased buffer duration slightly
            };
            waveOut.Init(buffer);
            waveOut.Play();

            var playback = new PeerPlayback { WaveOut = waveOut, Buffer = buffer, PeerId = peerId };
            if (_peerPlaybackStreams.TryAdd(peerId, playback))
            {
                 _debugLog.LogAudio($"Created audio playback for peer {peerId} with format {_pcm8KhzFormat}.");
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
            playback.WaveOut?.Stop();
            playback.WaveOut?.Dispose();
        }
        
        // Clean up transmission tracking
        _lastAudioReceived.TryRemove(peerId, out _);
        
        // Notify that peer stopped transmitting
        PeerTransmissionChanged?.Invoke(this, (peerId, false));
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

        float finalVolume = playback.IsMuted 
                            ? 0.0f 
                            : distanceFactor * playback.UiVolumeSetting * _volumeScale;
        
        finalVolume = Math.Clamp(finalVolume, 0.0f, 1.0f);

        try
        {
            playback.WaveOut.Volume = finalVolume;
            
            // Debug logging for volume scaling
            if (distance.HasValue)
            {
                _debugLog.LogAudio($"Volume for peer {playback.PeerId}: distance={distance.Value:F1}, distanceFactor={distanceFactor:F2}, uiVolume={playback.UiVolumeSetting:F2}, finalVolume={finalVolume:F2}");
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error applying volume settings for peer {playback.PeerId}: {ex.Message}");
        }
    }

    public void SetPeerMuteState(string peerId, bool isMuted)
    {
        if (_peerPlaybackStreams.TryGetValue(peerId, out var playback))
        {
            if (playback.IsMuted != isMuted) // Only act if state is different
            {
                playback.IsMuted = isMuted;
                ApplyVolumeSettings(playback);
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

    public void SetOverallVolumeScale(float scale)
    {
        _volumeScale = Math.Clamp(scale, 0.0f, 2.0f);
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
        _inputVolumeScale = Math.Clamp(scale, 0.0f, 2.0f);
    }

    public void CreatePeerAudioStream(string peerId)
    {
        if (!_peerPlaybackStreams.ContainsKey(peerId))
        {
            CreatePeerPlayback(peerId);
        }
    }

    public void Dispose()
    {
        StopCapture();

        foreach (var peerId in _peerPlaybackStreams.Keys.ToList())
        {
            RemovePeerAudioSource(peerId);
        }
        _peerPlaybackStreams.Clear();
        _microphoneCircularBuffer = null; // Release circular buffer
        _transmissionCheckTimer?.Dispose(); // Dispose transmission timer
        GC.SuppressFinalize(this);
    }

    private void CheckTransmissionTimeouts(object? state)
    {
        var now = DateTime.UtcNow;
        var peersToUpdate = new List<string>();
        
        // Check for peers that have stopped transmitting
        foreach (var kvp in _lastAudioReceived.ToList())
        {
            var peerId = kvp.Key;
            var lastReceived = kvp.Value;
            
            if ((now - lastReceived) > _transmissionTimeout)
            {
                peersToUpdate.Add(peerId);
                _lastAudioReceived.TryRemove(peerId, out _); // Remove from tracking
            }
        }
        
        // Notify of transmission status changes
        foreach (var peerId in peersToUpdate)
        {
            PeerTransmissionChanged?.Invoke(this, (peerId, false));
        }
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

// Helper class to read MuLaw from a stream for decoding
internal class MuLawWaveStream : WaveStream
{
    private readonly Stream _sourceStream;
    private readonly WaveFormat _waveFormat;

    public MuLawWaveStream(Stream sourceStream)
    {
        _sourceStream = sourceStream;
        _waveFormat = WaveFormat.CreateMuLawFormat(AudioService.PCMU_SAMPLE_RATE, AudioService.PCMU_CHANNELS);
    }

    public override WaveFormat WaveFormat => _waveFormat;

    public override long Length => _sourceStream.Length;

    public override long Position
    {
        get => _sourceStream.Position;
        set => _sourceStream.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _sourceStream.Read(buffer, offset, count);
    }
}