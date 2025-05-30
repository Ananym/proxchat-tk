#include "memreader.h"
#include <windows.h>
#include <string>
#include <vector>
#include <algorithm> // for std::find_if and std::remove
#include <cctype>    // for std::isspace
#include <chrono>    // for timestamp generation

// forward declaration of LogToFile from dllmain.cpp
extern void LogToFile(const std::string& message);

// helper function to trim trailing whitespace/nulls and remove internal nulls
std::string TrimStringData(const char* buffer, size_t length) {
    if (!buffer || length == 0) {
        return "";
    }

    // find the first double-null terminator (two consecutive null bytes)
    // this marks the actual end of the double-width string
    size_t stringLength = length; // default to entire buffer if no terminator found
    for (size_t i = 0; i < length - 1; i++) {
        if (buffer[i] == '\0' && buffer[i + 1] == '\0') {
            stringLength = i;
            break;
        }
    }

    // extract every other byte starting from index 0 (the actual character bytes)
    // up to the string terminator, skipping the null bytes between characters
    std::string result;
    for (size_t i = 0; i < stringLength; i += 2) {
        char c = buffer[i];
        if (c != '\0') { // only add non-null characters
            result += c;
        }
    }

    // trim trailing whitespace
    while (!result.empty() && std::isspace(static_cast<unsigned char>(result.back()))) {
        result.pop_back();
    }

    return result;
}

// helper function to safely read memory
bool SafeReadProcessMemory(LPCVOID lpBaseAddress, LPVOID lpBuffer, SIZE_T nSize, SIZE_T* lpNumberOfBytesRead) {
    // since we are injected into the target process, we can read directly.
    // however, using ReadProcessMemory might still be safer for handling potential page faults,
    // although direct pointer access is usually faster if stability is guaranteed.
    // for robustness, let's stick with ReadProcessMemory using the current process handle.
    // note: GetCurrentProcess() returns a pseudo-handle, which is fine for ReadProcessMemory
    // when reading the current process's memory.
    return ReadProcessMemory(GetCurrentProcess(), lpBaseAddress, lpBuffer, nSize, lpNumberOfBytesRead);
}

GameDataMessage CreateGameDataMessage() {
    static bool previousCallSucceeded = true;
    static uint32_t sequenceNumber = 0;
    
    GameDataMessage msg = {}; // zero-initialize
    msg.messageType = MSG_TYPE_GAME_DATA;
    msg.sequenceNumber = ++sequenceNumber;
    
    // get current timestamp in milliseconds
    auto now = std::chrono::system_clock::now();
    auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch());
    msg.timestampMs = static_cast<uint64_t>(ms.count());
    
    uintptr_t baseAddress = reinterpret_cast<uintptr_t>(GetModuleHandle(NULL));
    if (baseAddress == 0) {
        if (previousCallSucceeded) {
            LogToFile("Memory reading failed: Unable to get module handle");
            previousCallSucceeded = false;
        }
        msg.messageType = MSG_TYPE_ERROR;
        msg.flags = 0; // no success flag
        return msg;
    }

    bool success = true;
    SIZE_T bytesRead = 0;

    // read X coordinate
    uintptr_t xPtrAddr = baseAddress + 0x0029B4E4;
    uintptr_t xBasePtr = 0;
    if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(xPtrAddr), &xBasePtr, sizeof(xBasePtr), &bytesRead) && bytesRead == sizeof(xBasePtr)) {
        uintptr_t xAddr = xBasePtr + 0xFC;
        if (!SafeReadProcessMemory(reinterpret_cast<LPCVOID>(xAddr), &msg.x, sizeof(msg.x), &bytesRead) || bytesRead != sizeof(msg.x)) {
            success = false;
        }
    } else {
        success = false;
    }

    // read Y coordinate
    uintptr_t yPtrAddr = baseAddress + 0x0029BF3C;
    uintptr_t yBasePtr = 0;
    if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(yPtrAddr), &yBasePtr, sizeof(yBasePtr), &bytesRead) && bytesRead == sizeof(yBasePtr)) {
        uintptr_t yAddr = yBasePtr + 0x108;
        if (!SafeReadProcessMemory(reinterpret_cast<LPCVOID>(yAddr), &msg.y, sizeof(msg.y), &bytesRead) || bytesRead != sizeof(msg.y)) {
            success = false;
        }
    } else {
        success = false;
    }

    // read map ID
    uintptr_t mapIdPtrAddr = baseAddress + 0x0027A764;
    uintptr_t mapIdBasePtr = 0;
    if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(mapIdPtrAddr), &mapIdBasePtr, sizeof(mapIdBasePtr), &bytesRead) && bytesRead == sizeof(mapIdBasePtr)) {
        uintptr_t mapIdAddr = mapIdBasePtr + 0x3F2;
        if (!SafeReadProcessMemory(reinterpret_cast<LPCVOID>(mapIdAddr), &msg.mapId, sizeof(msg.mapId), &bytesRead) || bytesRead != sizeof(msg.mapId)) {
            success = false;
        }
    } else {
        success = false;
    }

    // read map name
    const size_t mapNameBufferSize = 42;
    std::vector<char> mapNameBuffer(mapNameBufferSize);
    uintptr_t mapNamePtrAddr = baseAddress + 0x0029B4B4;
    uintptr_t mapNameBasePtr = 0;
    if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(mapNamePtrAddr), &mapNameBasePtr, sizeof(mapNameBasePtr), &bytesRead) && bytesRead == sizeof(mapNameBasePtr)) {
        uintptr_t mapNameAddr = mapNameBasePtr + 0xF8;
        if (SafeReadProcessMemory(reinterpret_cast<LPCVOID>(mapNameAddr), mapNameBuffer.data(), mapNameBufferSize, &bytesRead)) {
            std::string mapName = TrimStringData(mapNameBuffer.data(), bytesRead);
            // copy to fixed-size buffer, ensuring null termination
            strncpy_s(msg.mapName, sizeof(msg.mapName), mapName.c_str(), sizeof(msg.mapName) - 1);
            msg.mapName[sizeof(msg.mapName) - 1] = '\0';
        } else {
            success = false;
        }
    } else {
        success = false;
    }

    // read character name
    const size_t charNameBufferSize = 24;
    std::vector<char> charNameBuffer(charNameBufferSize);
    uintptr_t charNamePtrAddr = baseAddress + 0x001A2DA4;
    uintptr_t charNameAddr = 0;
    if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(charNamePtrAddr), &charNameAddr, sizeof(charNameAddr), &bytesRead) && bytesRead == sizeof(charNameAddr)) {
        if (SafeReadProcessMemory(reinterpret_cast<LPCVOID>(charNameAddr), charNameBuffer.data(), charNameBufferSize, &bytesRead)) {
            std::string characterName = TrimStringData(charNameBuffer.data(), bytesRead);
            // copy to fixed-size buffer, ensuring null termination
            strncpy_s(msg.characterName, sizeof(msg.characterName), characterName.c_str(), sizeof(msg.characterName) - 1);
            msg.characterName[sizeof(msg.characterName) - 1] = '\0';
        } else {
            success = false;
        }
    } else {
        success = false;
    }

    // set flags based on success
    if (success) {
        msg.flags |= FLAG_SUCCESS | FLAG_POSITION_VALID;
    } else {
        msg.messageType = MSG_TYPE_ERROR;
        msg.flags = 0;
    }

    // log state transitions
    if (success != previousCallSucceeded) {
        if (success) {
            LogToFile("Memory reading recovered: All data read successfully");
        } else {
            LogToFile("Memory reading failed");
        }
        previousCallSucceeded = success;
    }

    return msg;
} 