using System;
using ProxChatClient.Services;
using OpusSharp.Core;

namespace ProxChatClient.Services;

/// <summary>
/// service for opus audio encoding and decoding using OpusSharp
/// provides a wrapper around the native opus codec with proper error handling
/// </summary>
public class OpusCodecService : IDisposable
{
    private readonly OpusEncoder _encoder;
    private readonly OpusDecoder _decoder;
    private readonly DebugLogService _debugLog;
    private readonly int _sampleRate;
    private readonly int _frameSize;
    private bool _disposed = false;

    internal const int DEFAULT_OPUS_SAMPLE_RATE = 48000;
    internal const int OPUS_CHANNELS = 1; // mono for voice chat
    internal const int OPUS_FRAME_SIZE_MS = 20; // standard WebRTC packet size
    internal const int OPUS_MAX_PACKET_SIZE = 1276; // max opus packet size

    public int SampleRate => _sampleRate;
    public int FrameSize => _frameSize;

    public OpusCodecService(DebugLogService debugLog, int sampleRate = DEFAULT_OPUS_SAMPLE_RATE)
        : this(debugLog, sampleRate, OpusPredefinedValues.OPUS_APPLICATION_VOIP)
    {
    }

    public OpusCodecService(DebugLogService debugLog, int sampleRate, OpusPredefinedValues applicationMode)
    {
        _debugLog = debugLog;
        _sampleRate = sampleRate;
        
        // 20ms frame = sampleRate * 0.02
        _frameSize = _sampleRate * OPUS_FRAME_SIZE_MS / 1000;
        
        try
        {
            _encoder = new OpusEncoder(_sampleRate, OPUS_CHANNELS, applicationMode);
            _decoder = new OpusDecoder(_sampleRate, OPUS_CHANNELS);
            
            string appModeStr = applicationMode switch
            {
                OpusPredefinedValues.OPUS_APPLICATION_VOIP => "VOIP",
                OpusPredefinedValues.OPUS_APPLICATION_AUDIO => "AUDIO", 
                OpusPredefinedValues.OPUS_APPLICATION_RESTRICTED_LOWDELAY => "RESTRICTED_LOWDELAY",
                _ => $"UNKNOWN({applicationMode})"
            };
            
            _debugLog.LogAudio($"OpusSharp codec initialized: {_sampleRate}Hz, {OPUS_CHANNELS} channel(s), frame size: {_frameSize} samples, mode: {appModeStr}");
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Failed to initialize OpusSharp codec: {ex.Message}");
            throw new InvalidOperationException($"Failed to initialize Opus codec: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// encode pcm samples to opus packet
    /// </summary>
    public byte[] Encode(short[] pcmSamples, int sampleCount)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpusCodecService));
        
        if (pcmSamples == null)
        {
            throw new ArgumentNullException(nameof(pcmSamples));
        }
        
        if (sampleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount, "Sample count cannot be negative");
        }
        
        if (sampleCount > pcmSamples.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount, $"Sample count ({sampleCount}) exceeds array length ({pcmSamples.Length})");
        }
        
        try
        {
            byte[] pcmBytes = new byte[sampleCount * 2]; // 2 bytes per 16-bit sample
            Buffer.BlockCopy(pcmSamples, 0, pcmBytes, 0, pcmBytes.Length);
            
            byte[] opusPacket = new byte[OPUS_MAX_PACKET_SIZE];
            
            int encodedBytes = _encoder.Encode(pcmBytes, sampleCount, opusPacket, opusPacket.Length);
            
            if (encodedBytes > 0)
            {
                // return only the used portion of the buffer
                byte[] result = new byte[encodedBytes];
                Array.Copy(opusPacket, result, encodedBytes);
                return result;
            }
            else
            {
                _debugLog.LogAudio($"OpusSharp encoding returned {encodedBytes} bytes");
                return new byte[0]; // return empty array for silence
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"OpusSharp encoding error: {ex.Message}");
            throw new InvalidOperationException($"Opus encoding failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// decode opus packet to pcm samples
    /// </summary>
    public short[] Decode(byte[] opusPacket, int packetLength)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpusCodecService));
        
        if (opusPacket == null)
        {
            throw new ArgumentNullException(nameof(opusPacket));
        }
        
        if (packetLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(packetLength), packetLength, "Packet length cannot be negative");
        }
        
        if (packetLength > opusPacket.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(packetLength), packetLength, $"Packet length ({packetLength}) exceeds array length ({opusPacket.Length})");
        }
        
        try
        {
            if (packetLength == 0)
            {
                return new short[_frameSize]; // return silence
            }
            
            byte[] decodedBytes = new byte[_frameSize * 2]; // 2 bytes per 16-bit sample
            
            int decodedSamples = _decoder.Decode(opusPacket, packetLength, decodedBytes, _frameSize, false);
            
            if (decodedSamples > 0)
            {
                short[] pcmSamples = new short[decodedSamples];
                Buffer.BlockCopy(decodedBytes, 0, pcmSamples, 0, decodedSamples * 2);
                return pcmSamples;
            }
            else
            {
                _debugLog.LogAudio($"OpusSharp decoding returned {decodedSamples} samples");
                return new short[_frameSize]; // return silence on decode failure
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"OpusSharp decoding error: {ex.Message}");
            // return silence on decode error to maintain audio stream continuity
            return new short[_frameSize];
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
        if (byteCount % 2 != 0)
        {
            byteCount--; // drop the last odd byte
        }
        
        int sampleCount = byteCount / 2;
        short[] samples = new short[sampleCount];
        
        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, i * 2);
        }
        
        return samples;
    }

    /// <summary>
    /// convert short array to byte array for audio processing
    /// </summary>
    public static byte[] ShortsToBytes(short[] samples, int sampleCount)
    {
        if (samples == null)
        {
            throw new ArgumentNullException(nameof(samples));
        }
        
        if (sampleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount, "Sample count cannot be negative");
        }
        
        if (sampleCount > samples.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount, $"Sample count ({sampleCount}) exceeds array length ({samples.Length})");
        }
        
        byte[] bytes = new byte[sampleCount * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        try
        {
            _encoder?.Dispose();
            _decoder?.Dispose();
            _debugLog.LogAudio("OpusSharp codec disposed");
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error disposing OpusSharp codec: {ex.Message}");
        }
        
        _disposed = true;
        GC.SuppressFinalize(this);
    }
} 