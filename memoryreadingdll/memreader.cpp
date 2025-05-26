#include "memreader.h"
#include <windows.h>
#include <string>
#include <vector>
#include <sstream> // For building the JSON string
#include <iomanip> // For formatting JSON output if needed
#include <algorithm> // For std::find_if and std::remove
#include <cctype>    // For std::isspace
#include <chrono>    // For timestamp generation

// Forward declaration of LogToFile from dllmain.cpp
// WARNING: This assumes LogToFile is accessible. If memreader.cpp is compiled
// separately without access to dllmain's implementation, this will fail linking.
// A better approach would be to pass a logging function pointer or use a shared logging header.
// For now, we'll assume it's accessible for debugging.
extern void LogToFile(const std::string& message);

// Helper function to trim trailing whitespace/nulls and remove internal nulls
std::string TrimStringData(const char* buffer, size_t length) {
    if (!buffer || length == 0) {
        return "";
    }

    // Find the first double-null terminator (two consecutive null bytes)
    // This marks the actual end of the double-width string
    size_t stringLength = length; // Default to entire buffer if no terminator found
    for (size_t i = 0; i < length - 1; i++) {
        if (buffer[i] == '\0' && buffer[i + 1] == '\0') {
            stringLength = i;
            break;
        }
    }

    // Extract every other byte starting from index 0 (the actual character bytes)
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


// Helper function to safely read memory
bool SafeReadProcessMemory(LPCVOID lpBaseAddress, LPVOID lpBuffer, SIZE_T nSize, SIZE_T* lpNumberOfBytesRead) {
    // Since we are injected into the target process, we can read directly.
    // However, using ReadProcessMemory might still be safer for handling potential page faults,
    // although direct pointer access is usually faster if stability is guaranteed.
    // For robustness, let's stick with ReadProcessMemory using the current process handle.
    // Note: GetCurrentProcess() returns a pseudo-handle, which is fine for ReadProcessMemory
    // when reading the current process's memory.
    return ReadProcessMemory(GetCurrentProcess(), lpBaseAddress, lpBuffer, nSize, lpNumberOfBytesRead);
}


std::string ReadMemoryValuesToJson() {
    static bool previousCallSucceeded = true; // track state across calls
    
    uintptr_t baseAddress = reinterpret_cast<uintptr_t>(GetModuleHandle(NULL));
    if (baseAddress == 0) {
        if (previousCallSucceeded) {
            LogToFile("Memory reading failed: Unable to get module handle");
            previousCallSucceeded = false;
        }
        return R"({"success": false, "error": "Failed to get module handle"})";
    }

    int x = 0;
    int y = 0;
    uint16_t mapId = 0;
    std::string mapName = "";
    std::string characterName = "";
    bool success = true;
    std::string errorMessage = "";
    SIZE_T bytesRead = 0;

    // read X coordinate
    uintptr_t xPtrAddr = baseAddress + 0x0029B4E4;
    uintptr_t xBasePtr = 0;
    if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(xPtrAddr), &xBasePtr, sizeof(xBasePtr), &bytesRead) && bytesRead == sizeof(xBasePtr)) {
        uintptr_t xAddr = xBasePtr + 0xFC;
        if (!SafeReadProcessMemory(reinterpret_cast<LPCVOID>(xAddr), &x, sizeof(x), &bytesRead) || bytesRead != sizeof(x)) {
            success = false;
            errorMessage = "Failed to read X coordinate value";
        }
    } else if (success) {
        success = false;
        errorMessage = "Failed to read X coordinate base pointer";
    }

    // read Y coordinate
    uintptr_t yPtrAddr = baseAddress + 0x0029BF3C;
    uintptr_t yBasePtr = 0;
    if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(yPtrAddr), &yBasePtr, sizeof(yBasePtr), &bytesRead) && bytesRead == sizeof(yBasePtr)) {
        uintptr_t yAddr = yBasePtr + 0x108;
        if (!SafeReadProcessMemory(reinterpret_cast<LPCVOID>(yAddr), &y, sizeof(y), &bytesRead) || bytesRead != sizeof(y)) {
            success = false;
            errorMessage = "Failed to read Y coordinate value";
        }
    } else if (success) {
        success = false;
        errorMessage = "Failed to read Y coordinate base pointer";
    }

    // read map ID
    uintptr_t mapIdPtrAddr = baseAddress + 0x0027A764;
    uintptr_t mapIdBasePtr = 0;
    if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(mapIdPtrAddr), &mapIdBasePtr, sizeof(mapIdBasePtr), &bytesRead) && bytesRead == sizeof(mapIdBasePtr)) {
        uintptr_t mapIdAddr = mapIdBasePtr + 0x3F2;
        if (!SafeReadProcessMemory(reinterpret_cast<LPCVOID>(mapIdAddr), &mapId, sizeof(mapId), &bytesRead) || bytesRead != sizeof(mapId)) {
            success = false;
            errorMessage = "Failed to read map ID value";
        }
    } else if (success) {
        success = false;
        errorMessage = "Failed to read map ID base pointer";
    }

    // read map name (21 chars * 2 bytes/char = 42 bytes)
    const size_t mapNameBufferSize = 42;
    std::vector<char> mapNameBuffer(mapNameBufferSize);
    uintptr_t mapNamePtrAddr = baseAddress + 0x0029B4B4;
    uintptr_t mapNameBasePtr = 0;
    if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(mapNamePtrAddr), &mapNameBasePtr, sizeof(mapNameBasePtr), &bytesRead) && bytesRead == sizeof(mapNameBasePtr)) {
        uintptr_t mapNameAddr = mapNameBasePtr + 0xF8;
        if (SafeReadProcessMemory(reinterpret_cast<LPCVOID>(mapNameAddr), mapNameBuffer.data(), mapNameBufferSize, &bytesRead)) {
            mapName = TrimStringData(mapNameBuffer.data(), bytesRead);
        } else {
            success = false;
            errorMessage = "Failed to read map name value";
        }
    } else if (success) {
        success = false;
        errorMessage = "Failed to read map name base pointer";
    }

    // read character name (12 chars * 2 bytes/char = 24 bytes)
    const size_t charNameBufferSize = 24;
    std::vector<char> charNameBuffer(charNameBufferSize);
    uintptr_t charNamePtrAddr = baseAddress + 0x001A2DA4;
    uintptr_t charNameAddr = 0;
    if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(charNamePtrAddr), &charNameAddr, sizeof(charNameAddr), &bytesRead) && bytesRead == sizeof(charNameAddr)) {
        if (SafeReadProcessMemory(reinterpret_cast<LPCVOID>(charNameAddr), charNameBuffer.data(), charNameBufferSize, &bytesRead)) {
            characterName = TrimStringData(charNameBuffer.data(), bytesRead);
        } else {
            success = false;
            errorMessage = "Failed to read character name value";
        }
    } else if (success) {
        success = false;
        errorMessage = "Failed to read character name pointer";
    }

    // log state transitions
    if (success != previousCallSucceeded) {
        if (success) {
            LogToFile("Memory reading recovered: All data read successfully");
        } else {
            LogToFile("Memory reading failed: " + errorMessage);
        }
        previousCallSucceeded = success;
    }

    // generate iso timestamp
    auto now = std::chrono::system_clock::now();
    auto time_t = std::chrono::system_clock::to_time_t(now);
    auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch()) % 1000;
    
    std::tm utc_tm;
    gmtime_s(&utc_tm, &time_t);
    
    std::ostringstream timestampStream;
    timestampStream << std::put_time(&utc_tm, "%Y-%m-%dT%H:%M:%S")
                   << '.' << std::setfill('0') << std::setw(3) << ms.count() << 'Z';
    std::string timestamp = timestampStream.str();

    // construct JSON response
    std::ostringstream jsonStream;
    if (success) {
        jsonStream << R"({"success": true, "timestamp": ")" << timestamp << R"(", "data": {)";
        jsonStream << R"("x": )" << x << ", ";
        jsonStream << R"("y": )" << y << ", ";
        jsonStream << R"("mapId": )" << mapId << ", ";
        jsonStream << R"("mapName": ")" << mapName << R"(", )";
        jsonStream << R"("characterName": ")" << characterName << R"(")";
        jsonStream << R"(}})";
        return jsonStream.str();
    } else {
        jsonStream << R"({"success": false, "timestamp": ")" << timestamp << R"(", "error": ")" << errorMessage << R"("})";
        return jsonStream.str();
    }
} 