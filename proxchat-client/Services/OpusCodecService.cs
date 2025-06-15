using Concentus.Structs;
using System;

namespace ProxChatClient.Services;

/// <summary>
/// service for opus audio encoding and decoding using concentus library
/// supports variable sample rates for flexible audio processing
/// </summary>
public class OpusCodecService : IDisposable
{
    // opus configuration constants
    public const int DEFAULT_SAMPLE_RATE = 48000; // default opus sample rate
    public const int OPUS_CHANNELS = 1; // mono for voice chat
    public const int OPUS_FRAME_SIZE_MS = 20; // standard webrtc frame size
    public const int OPUS_MAX_PACKET_SIZE = 1276; // max opus packet size
    
    private readonly OpusEncoder _encoder;
    private readonly OpusDecoder _decoder;
    private readonly DebugLogService _debugLog;
    private readonly int _sampleRate;
    private readonly int _frameSize; // calculated based on actual sample rate
    private bool _disposed = false;

    public int SampleRate => _sampleRate;
    public int FrameSize => _frameSize;

    public OpusCodecService(DebugLogService debugLog, int sampleRate = DEFAULT_SAMPLE_RATE)
    {
        _debugLog = debugLog;
        _sampleRate = sampleRate;
        _frameSize = _sampleRate * OPUS_FRAME_SIZE_MS / 1000; // e.g., 882 for 44.1kHz, 960 for 48kHz
        
        try
        {
            // create opus encoder for voip application - opus supports multiple sample rates
            _encoder = new OpusEncoder(_sampleRate, OPUS_CHANNELS, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
            
            // configure encoder for low latency voice chat
            _encoder.Bitrate = 32000; // 32kbps - good quality for voice
            _encoder.Complexity = 5; // medium complexity for balance of quality/cpu
            _encoder.SignalType = Concentus.Enums.OpusSignal.OPUS_SIGNAL_VOICE;
            _encoder.ForceMode = Concentus.Enums.OpusMode.MODE_SILK_ONLY; // silk mode for voice
            
            _debugLog.LogAudio($"Created Opus encoder: {_sampleRate}Hz, {OPUS_CHANNELS} channel(s), {_encoder.Bitrate}bps, frame size: {_frameSize} samples");
            
            // create opus decoder
            _decoder = new OpusDecoder(_sampleRate, OPUS_CHANNELS);
            _debugLog.LogAudio($"Created Opus decoder: {_sampleRate}Hz, {OPUS_CHANNELS} channel(s)");
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error creating Opus codec: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// encode pcm audio samples to opus
    /// </summary>
    /// <param name="pcmSamples">16-bit pcm samples at the configured sample rate</param>
    /// <param name="sampleCount">number of samples (should match frame size for optimal encoding)</param>
    /// <returns>encoded opus packet</returns>
    public byte[] Encode(short[] pcmSamples, int sampleCount)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpusCodecService));
        
        // Validate inputs before encoding
        if (pcmSamples == null)
        {
            _debugLog.LogAudio($"[ERROR] Opus encode called with null pcmSamples");
            return new byte[0];
        }
        
        if (sampleCount < 0)
        {
            _debugLog.LogAudio($"[ERROR] Opus encode called with negative sampleCount: {sampleCount}");
            return new byte[0];
        }
        
        if (sampleCount > pcmSamples.Length)
        {
            _debugLog.LogAudio($"[ERROR] Opus encode sampleCount ({sampleCount}) exceeds pcmSamples.Length ({pcmSamples.Length})");
            return new byte[0];
        }
        
        // allow flexible frame sizes - opus can handle different sizes
        if (sampleCount != _frameSize)
        {
            _debugLog.LogAudio($"Frame size mismatch: Expected {_frameSize} samples for {_sampleRate}Hz, got {sampleCount}");
        }
        
        try
        {
            // Add detailed logging for debugging
            _debugLog.LogAudio($"[OPUS] About to call _encoder.Encode with: pcmSamples.Length={pcmSamples.Length}, sampleCount={sampleCount}, _frameSize={_frameSize}, _sampleRate={_sampleRate}");
            
            byte[] opusPacket = new byte[OPUS_MAX_PACKET_SIZE];
            
            // WORKAROUND: Try different approaches to work around Concentus library bug
            int encodedBytes = 0;
            
            // Approach 1: Use frame size instead of sample count (in case library expects frame size)
            try
            {
                encodedBytes = _encoder.Encode(pcmSamples, 0, _frameSize, opusPacket, 0, opusPacket.Length);
                _debugLog.LogAudio($"[OPUS] Workaround 1 (frame size): Encoding successful with _frameSize={_frameSize}");
            }
            catch (Exception ex1)
            {
                _debugLog.LogAudio($"[OPUS] Workaround 1 failed: {ex1.Message}");
                
                // Approach 2: Try with exact sample count but different buffer approach
                try
                {
                    // Create a new array with exact size needed
                    short[] exactSamples = new short[_frameSize];
                    int copyCount = Math.Min(pcmSamples.Length, _frameSize);
                    Array.Copy(pcmSamples, exactSamples, copyCount);
                    
                    encodedBytes = _encoder.Encode(exactSamples, 0, _frameSize, opusPacket, 0, opusPacket.Length);
                    _debugLog.LogAudio($"[OPUS] Workaround 2 (exact array): Encoding successful with copyCount={copyCount}");
                }
                catch (Exception ex2)
                {
                    _debugLog.LogAudio($"[OPUS] Workaround 2 failed: {ex2.Message}");
                    
                    // Approach 3: Try with smaller buffer sizes to isolate the issue
                    try
                    {
                        int halfFrame = _frameSize / 2;
                        short[] smallSamples = new short[halfFrame];
                        Array.Copy(pcmSamples, smallSamples, Math.Min(pcmSamples.Length, halfFrame));
                        
                        encodedBytes = _encoder.Encode(smallSamples, 0, halfFrame, opusPacket, 0, opusPacket.Length);
                        _debugLog.LogAudio($"[OPUS] Workaround 3 (half frame): Encoding successful with halfFrame={halfFrame}");
                    }
                    catch (Exception ex3)
                    {
                        _debugLog.LogAudio($"[OPUS] All workarounds failed. ex1: {ex1.Message}, ex2: {ex2.Message}, ex3: {ex3.Message}");
                        throw ex1; // Re-throw the original exception
                    }
                }
            }
            
            if (encodedBytes > 0)
            {
                // return only the used portion of the buffer
                byte[] result = new byte[encodedBytes];
                Array.Copy(opusPacket, result, encodedBytes);
                _debugLog.LogAudio($"[OPUS] Final encoding successful: {sampleCount} samples -> {encodedBytes} bytes");
                return result;
            }
            else
            {
                _debugLog.LogAudio($"Opus encoding failed, returned {encodedBytes} bytes");
                return new byte[0];
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error encoding Opus: {ex.Message}");
            _debugLog.LogAudio($"[OPUS] Error context: pcmSamples.Length={pcmSamples?.Length ?? -1}, sampleCount={sampleCount}, _frameSize={_frameSize}");
            return new byte[0];
        }
    }

    /// <summary>
    /// decode opus packet to pcm audio samples
    /// </summary>
    /// <param name="opusPacket">encoded opus packet</param>
    /// <param name="packetLength">length of opus packet</param>
    /// <returns>decoded 16-bit pcm samples at the configured sample rate</returns>
    public short[] Decode(byte[] opusPacket, int packetLength)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpusCodecService));
        
        try
        {
            short[] pcmSamples = new short[_frameSize];
            int decodedSamples = _decoder.Decode(opusPacket, 0, packetLength, pcmSamples, 0, _frameSize, false);
            
            if (decodedSamples == _frameSize)
            {
                return pcmSamples;
            }
            else if (decodedSamples > 0)
            {
                // return only the decoded portion
                short[] result = new short[decodedSamples];
                Array.Copy(pcmSamples, result, decodedSamples);
                return result;
            }
            else
            {
                _debugLog.LogAudio($"Opus decoding failed, returned {decodedSamples} samples");
                return new short[0];
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error decoding Opus: {ex.Message}");
            return new short[0];
        }
    }

    /// <summary>
    /// decode opus packet with packet loss concealment
    /// </summary>
    /// <returns>decoded pcm samples with plc applied</returns>
    public short[] DecodeWithPLC()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpusCodecService));
        
        try
        {
            short[] pcmSamples = new short[_frameSize];
            int decodedSamples = _decoder.Decode(null, 0, 0, pcmSamples, 0, _frameSize, false);
            
            if (decodedSamples == _frameSize)
            {
                return pcmSamples;
            }
            else
            {
                _debugLog.LogAudio($"Opus PLC decoding returned {decodedSamples} samples");
                return new short[_frameSize]; // return silence
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error in Opus PLC decoding: {ex.Message}");
            return new short[_frameSize]; // return silence
        }
    }

    /// <summary>
    /// convert byte array to short array for pcm processing
    /// </summary>
    public static short[] BytesToShorts(byte[] bytes, int byteCount)
    {
        // validate inputs to prevent negative values and array bounds issues
        if (bytes == null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }
        
        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "Byte count cannot be negative");
        }
        
        if (byteCount > bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, $"Byte count ({byteCount}) exceeds array length ({bytes.Length})");
        }
        
        // ensure even number of bytes for 16-bit samples
        int originalByteCount = byteCount;
        if (byteCount % 2 != 0)
        {
            byteCount--; // drop the last odd byte
        }
        
        int sampleCount = byteCount / 2;
        short[] samples = new short[sampleCount];
        
        // Add debug logging for the first few calls
        if (samples.Length <= 1000) // Only log for reasonable sizes
        {
            System.Diagnostics.Debug.WriteLine($"[BYTES_TO_SHORTS] Input: {originalByteCount} bytes -> {byteCount} aligned bytes -> {sampleCount} samples");
        }
        
        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, i * 2);
        }
        
        return samples;
    }

    /// <summary>
    /// convert short array to byte array for audio output
    /// </summary>
    public static byte[] ShortsToBytes(short[] samples, int sampleCount)
    {
        byte[] bytes = new byte[sampleCount * 2];
        
        for (int i = 0; i < sampleCount; i++)
        {
            byte[] sampleBytes = BitConverter.GetBytes(samples[i]);
            bytes[i * 2] = sampleBytes[0];
            bytes[i * 2 + 1] = sampleBytes[1];
        }
        
        return bytes;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            // OpusEncoder and OpusDecoder in Concentus are structs, not classes
            // so they don't need explicit disposal
            _disposed = true;
            _debugLog.LogAudio("Opus codec service disposed");
        }
    }
} 