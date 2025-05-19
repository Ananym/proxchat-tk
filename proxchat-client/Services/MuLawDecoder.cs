using System;

namespace ProxChatClient.Services;

public static class MuLawDecoder
{
    private static readonly short[] MuLawToLinearTable = new short[256];

    static MuLawDecoder()
    {
        // Initialize the conversion table
        for (int i = 0; i < 256; i++)
        {
            MuLawToLinearTable[i] = Decode((byte)i);
        }
    }

    public static short MuLawToLinearSample(byte mulaw)
    {
        return MuLawToLinearTable[mulaw];
    }

    private static short Decode(byte mulaw)
    {
        // Convert MuLaw to linear PCM
        mulaw = (byte)~mulaw;

        int sign = (mulaw & 0x80) >> 7;
        int exponent = (mulaw & 0x70) >> 4;
        int mantissa = mulaw & 0x0F;

        int linear = mantissa << (exponent + 3);
        linear |= 0x84 << exponent;
        linear = sign == 0 ? linear : -linear;

        return (short)linear;
    }
} 