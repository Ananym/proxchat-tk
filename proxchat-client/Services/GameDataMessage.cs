using System;
using System.Runtime.InteropServices;

namespace ProxChatClient.Services;

// C# equivalent of the C++ GameDataMessage struct
// must match exactly with C++ version for binary compatibility
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GameDataMessage
{
    // timestamp (8 bytes) - starts on 8-byte boundary for alignment
    public ulong TimestampMs;       // milliseconds since epoch (UTC)
    
    // game data (40 bytes)
    public int X;                   // player x coordinate
    public int Y;                   // player y coordinate
    public ushort MapId;            // map identifier
    public ushort Reserved1;        // padding for alignment
    
    // strings with fixed sizes (null-terminated)
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] MapNameBytes;     // map name (15 chars + null terminator)
    
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
    public byte[] CharacterNameBytes; // character name (11 chars + null terminator)
    
    // status and padding (8 bytes)
    public uint Flags;              // bit flags: 0x01 = success
    public uint Reserved2;          // future use / padding
    
    // helper properties for string access
    public string MapName
    {
        get
        {
            if (MapNameBytes == null) return string.Empty;
            int nullIndex = Array.IndexOf(MapNameBytes, (byte)0);
            int length = nullIndex >= 0 ? nullIndex : MapNameBytes.Length;
            return System.Text.Encoding.UTF8.GetString(MapNameBytes, 0, length);
        }
    }
    
    public string CharacterName
    {
        get
        {
            if (CharacterNameBytes == null) return string.Empty;
            int nullIndex = Array.IndexOf(CharacterNameBytes, (byte)0);
            int length = nullIndex >= 0 ? nullIndex : CharacterNameBytes.Length;
            return System.Text.Encoding.UTF8.GetString(CharacterNameBytes, 0, length);
        }
    }
    
    public bool IsSuccess => (Flags & MessageFlags.Success) != 0;
    
    public DateTime Timestamp => DateTimeOffset.FromUnixTimeMilliseconds((long)TimestampMs).UtcDateTime;
}

// flag constants
public static class MessageFlags
{
    public const uint Success = 0x01;
} 