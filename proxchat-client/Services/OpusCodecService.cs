using Concentus.Structs;
using System;

namespace ProxChatClient.Services;

/// <summary>
/// service for opus audio encoding and decoding using concentus library
/// </summary>
public class OpusCodecService : IDisposable
{
    // opus configuration constants
    public const int OPUS_SAMPLE_RATE = 48000; // opus native sample rate
    public const int OPUS_CHANNELS = 1; // mono for voice chat
    public const int OPUS_FRAME_SIZE_MS = 20; // standard webrtc frame size
    public const int OPUS_FRAME_SIZE_SAMPLES = OPUS_SAMPLE_RATE * OPUS_FRAME_SIZE_MS / 1000; // 960 samples
    public const int OPUS_MAX_PACKET_SIZE = 1276; // max opus packet size
    
    private readonly OpusEncoder _encoder;
    private readonly OpusDecoder _decoder;
    private readonly DebugLogService _debugLog;
    private bool _disposed = false;

    public OpusCodecService(DebugLogService debugLog)
    {
        _debugLog = debugLog;
        
        try
        {
            // create opus encoder for voip application
            _encoder = new OpusEncoder(OPUS_SAMPLE_RATE, OPUS_CHANNELS, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
            
            // configure encoder for low latency voice chat
            _encoder.Bitrate = 32000; // 32kbps - good quality for voice
            _encoder.Complexity = 5; // medium complexity for balance of quality/cpu
            _encoder.SignalType = Concentus.Enums.OpusSignal.OPUS_SIGNAL_VOICE;
            _encoder.ForceMode = Concentus.Enums.OpusMode.MODE_SILK_ONLY; // silk mode for voice
            
            _debugLog.LogAudio($"Created Opus encoder: {OPUS_SAMPLE_RATE}Hz, {OPUS_CHANNELS} channel(s), {_encoder.Bitrate}bps");
            
            // create opus decoder
            _decoder = new OpusDecoder(OPUS_SAMPLE_RATE, OPUS_CHANNELS);
            _debugLog.LogAudio($"Created Opus decoder: {OPUS_SAMPLE_RATE}Hz, {OPUS_CHANNELS} channel(s)");
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
    /// <param name="pcmSamples">16-bit pcm samples at 48khz</param>
    /// <param name="sampleCount">number of samples (should be 960 for 20ms)</param>
    /// <returns>encoded opus packet</returns>
    public byte[] Encode(short[] pcmSamples, int sampleCount)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpusCodecService));
        
        if (sampleCount != OPUS_FRAME_SIZE_SAMPLES)
        {
            _debugLog.LogAudio($"Warning: Expected {OPUS_FRAME_SIZE_SAMPLES} samples, got {sampleCount}");
        }
        
        try
        {
            byte[] opusPacket = new byte[OPUS_MAX_PACKET_SIZE];
            int encodedBytes = _encoder.Encode(pcmSamples, 0, sampleCount, opusPacket, 0, opusPacket.Length);
            
            if (encodedBytes > 0)
            {
                // return only the used portion of the buffer
                byte[] result = new byte[encodedBytes];
                Array.Copy(opusPacket, result, encodedBytes);
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
            return new byte[0];
        }
    }

    /// <summary>
    /// decode opus packet to pcm audio samples
    /// </summary>
    /// <param name="opusPacket">encoded opus packet</param>
    /// <param name="packetLength">length of opus packet</param>
    /// <returns>decoded 16-bit pcm samples at 48khz</returns>
    public short[] Decode(byte[] opusPacket, int packetLength)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpusCodecService));
        
        try
        {
            short[] pcmSamples = new short[OPUS_FRAME_SIZE_SAMPLES];
            int decodedSamples = _decoder.Decode(opusPacket, 0, packetLength, pcmSamples, 0, OPUS_FRAME_SIZE_SAMPLES, false);
            
            if (decodedSamples == OPUS_FRAME_SIZE_SAMPLES)
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
            short[] pcmSamples = new short[OPUS_FRAME_SIZE_SAMPLES];
            int decodedSamples = _decoder.Decode(null, 0, 0, pcmSamples, 0, OPUS_FRAME_SIZE_SAMPLES, false);
            
            if (decodedSamples == OPUS_FRAME_SIZE_SAMPLES)
            {
                return pcmSamples;
            }
            else
            {
                _debugLog.LogAudio($"Opus PLC decoding returned {decodedSamples} samples");
                return new short[OPUS_FRAME_SIZE_SAMPLES]; // return silence
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogAudio($"Error in Opus PLC decoding: {ex.Message}");
            return new short[OPUS_FRAME_SIZE_SAMPLES]; // return silence
        }
    }

    /// <summary>
    /// convert byte array to short array for pcm processing
    /// </summary>
    public static short[] BytesToShorts(byte[] bytes, int byteCount)
    {
        int sampleCount = byteCount / 2;
        short[] samples = new short[sampleCount];
        
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