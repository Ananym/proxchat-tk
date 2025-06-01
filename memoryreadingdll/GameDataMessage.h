#pragma once
#include <cstdint>

// fixed-size message struct for named pipe communication
// total size: 64 bytes (cache-line friendly)
#pragma pack(push, 1)
struct GameDataMessage {
    // message header (8 bytes)
    uint32_t messageType;     // 0 = game data, 1 = error, 2 = heartbeat, 3 = handshake
    uint32_t sequenceNumber;  // incrementing counter for message ordering
    
    // timestamp (8 bytes)
    uint64_t timestampMs;     // milliseconds since epoch (UTC)
    
    // game data (40 bytes)
    int32_t x;                // player x coordinate
    int32_t y;                // player y coordinate
    uint16_t mapId;           // map identifier
    uint16_t reserved1;       // padding for alignment
    
    // strings with fixed sizes (null-terminated)
    char mapName[16];         // map name (15 chars + null terminator)
    char characterName[12];   // character name (11 chars + null terminator)
    
    // status and padding (8 bytes)
    uint32_t flags;           // bit flags: 0x01 = success, 0x02 = position_valid, etc.
    uint32_t reserved2;       // future use / padding to 64 bytes
};
#pragma pack(pop)

// message types
constexpr uint32_t MSG_TYPE_GAME_DATA = 0;
constexpr uint32_t MSG_TYPE_ERROR = 1;
constexpr uint32_t MSG_TYPE_HEARTBEAT = 2;
constexpr uint32_t MSG_TYPE_HANDSHAKE = 3;  // connection verification

// flag definitions
constexpr uint32_t FLAG_SUCCESS = 0x01;
constexpr uint32_t FLAG_POSITION_VALID = 0x02;

static_assert(sizeof(GameDataMessage) == 64, "GameDataMessage must be exactly 64 bytes"); 