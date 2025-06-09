#pragma once
#include <cstdint>

// fixed-size message struct for named pipe communication
// total size: 56 bytes (cache-line friendly, properly aligned)
#pragma pack(push, 1)
struct GameDataMessage {
    // timestamp (8 bytes) - starts on 8-byte boundary for alignment
    uint64_t timestampMs;     // milliseconds since epoch (UTC)
    
    // game data (40 bytes)
    int32_t x;                // player x coordinate
    int32_t y;                // player y coordinate
    uint16_t mapId;           // map identifier
    uint16_t gameId;          // game identifier (always 0 for this game)
    
    // strings with fixed sizes (null-terminated)
    char mapName[16];         // map name (15 chars + null terminator)
    char characterName[12];   // character name (11 chars + null terminator)
    
    // status and padding (8 bytes)
    uint32_t flags;           // bit flags: 0x01 = success
    uint32_t reserved2;       // future use / padding
};
#pragma pack(pop)

// flag definitions
constexpr uint32_t FLAG_SUCCESS = 0x01;

static_assert(sizeof(GameDataMessage) == 56, "GameDataMessage must be exactly 56 bytes"); 