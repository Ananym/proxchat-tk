#include "memreader.h"
#include <windows.h>
#include <string>
#include <vector>
#include <sstream> // For building the JSON string
#include <iomanip> // For formatting JSON output if needed
#include <algorithm> // For std::find_if and std::remove
#include <cctype>    // For std::isspace

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

    // Find the last non-null character considering the actual bytes read (length)
    const char* end = buffer + length - 1;
    while (end >= buffer && *end == '\0') {
        end--;
    }
    size_t validLength = (end >= buffer) ? (end - buffer + 1) : 0;

    if (validLength == 0) {
        return ""; // String was all nulls or empty
    }

    // Find the last non-whitespace character from the potentially null-trimmed string
    const char* ws_end = buffer + validLength - 1;
    while (ws_end >= buffer && std::isspace(static_cast<unsigned char>(*ws_end))) {
        ws_end--;
    }
    size_t trimmedLength = (ws_end >= buffer) ? (ws_end - buffer + 1) : 0;

    if (trimmedLength == 0) {
        return ""; // String was all whitespace/nulls
    }

    // Create string from the valid, whitespace-trimmed range
    std::string result(buffer, trimmedLength);

    // Remove any embedded null characters ('\0') from the result
    result.erase(std::remove(result.begin(), result.end(), '\0'), result.end());

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
    // LogToFile("ReadMemoryValuesToJson: Entering function."); // Removed entry log
    uintptr_t baseAddress = reinterpret_cast<uintptr_t>(GetModuleHandle(NULL));
    if (baseAddress == 0) {
        LogToFile("ReadMemoryValuesToJson: Failed to get module handle."); // Keep this critical error log
        return R"({"success": false, "error": "Failed to get module handle"})";
    }
    // Removed base address log

    int x = 0;
    int y = 0;
    uint16_t mapId = 0;
    std::string mapName = "";
    std::string characterName = "";
    bool success = true;
    std::string errorMessage = ""; // Keep track of the first error encountered
    SIZE_T bytesRead = 0;

    // --- Read X ---
    uintptr_t xPtrAddr = baseAddress + 0x0029B4E4;
    uintptr_t xBasePtr = 0;
    // LogToFile("ReadMemoryValuesToJson: Reading X base pointer..."); // Removed pre-read log
    if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(xPtrAddr), &xBasePtr, sizeof(xBasePtr), &bytesRead) && bytesRead == sizeof(xBasePtr)) {
        uintptr_t xAddr = xBasePtr + 0xFC;
        // LogToFile("ReadMemoryValuesToJson: Reading X value..."); // Removed pre-read log
        if (!SafeReadProcessMemory(reinterpret_cast<LPCVOID>(xAddr), &x, sizeof(x), &bytesRead) || bytesRead != sizeof(x)) {
            success = false;
            errorMessage = "Failed to read X value";
            LogToFile("ReadMemoryValuesToJson: Failed to read X value."); // Keep failure log
            // LogToFile("ReadMemoryValuesToJson: Failed to read X value."); // Removed duplicate
        } //else {
          //   LogToFile("ReadMemoryValuesToJson: Successfully read X."); // Removed success log
        //}
    } else if (success) {
        success = false;
        errorMessage = "Failed to read X base pointer";
        LogToFile("ReadMemoryValuesToJson: Failed to read X base pointer."); // Keep failure log
    }

    // --- Read Y ---
    uintptr_t yPtrAddr = baseAddress + 0x0029BF3C;
    uintptr_t yBasePtr = 0;
    // LogToFile("ReadMemoryValuesToJson: Reading Y base pointer..."); // Removed pre-read log
     if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(yPtrAddr), &yBasePtr, sizeof(yBasePtr), &bytesRead) && bytesRead == sizeof(yBasePtr)) {
        uintptr_t yAddr = yBasePtr + 0x108;
        // LogToFile("ReadMemoryValuesToJson: Reading Y value..."); // Removed pre-read log
        if (!SafeReadProcessMemory(reinterpret_cast<LPCVOID>(yAddr), &y, sizeof(y), &bytesRead) || bytesRead != sizeof(y)) {
            success = false;
            errorMessage = "Failed to read Y value";
            LogToFile("ReadMemoryValuesToJson: Failed to read Y value."); // Keep failure log
        } //else {
            //LogToFile("ReadMemoryValuesToJson: Successfully read Y."); // Removed success log
        //}
    } else if (success) {
        success = false;
        errorMessage = "Failed to read Y base pointer";
        LogToFile("ReadMemoryValuesToJson: Failed to read Y base pointer."); // Keep failure log
    }

    // --- Read Map ID (uint16_t) ---
    uintptr_t mapIdPtrAddr = baseAddress + 0x0027A764;
    uintptr_t mapIdBasePtr = 0;
    // LogToFile("ReadMemoryValuesToJson: Reading mapId base pointer..."); // Removed pre-read log
    if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(mapIdPtrAddr), &mapIdBasePtr, sizeof(mapIdBasePtr), &bytesRead) && bytesRead == sizeof(mapIdBasePtr)) {
        uintptr_t mapIdAddr = mapIdBasePtr + 0x3F2;
        // LogToFile("ReadMemoryValuesToJson: Reading mapId value..."); // Removed pre-read log
        if (!SafeReadProcessMemory(reinterpret_cast<LPCVOID>(mapIdAddr), &mapId, sizeof(mapId), &bytesRead) || bytesRead != sizeof(mapId)) {
            success = false;
            errorMessage = "Failed to read mapId value";
            LogToFile("ReadMemoryValuesToJson: Failed to read mapId value."); // Keep failure log
        } //else {
            // LogToFile("ReadMemoryValuesToJson: Successfully read mapId."); // Removed success log
        //}
    } else if (success) {
        success = false;
        errorMessage = "Failed to read mapId base pointer";
        LogToFile("ReadMemoryValuesToJson: Failed to read mapId base pointer."); // Keep failure log
    }


    // --- Read mapName (previously mapId) (21 chars * 2 bytes/char = 42 bytes) ---
    const size_t mapNameBufferSize = 42;
    std::vector<char> mapNameBuffer(mapNameBufferSize);
    uintptr_t mapNamePtrAddr = baseAddress + 0x0029B4B4;
    uintptr_t mapNameBasePtr = 0;
    // LogToFile("ReadMemoryValuesToJson: Reading mapName base pointer..."); // Removed pre-read log
     if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(mapNamePtrAddr), &mapNameBasePtr, sizeof(mapNameBasePtr), &bytesRead) && bytesRead == sizeof(mapNameBasePtr)) {
        uintptr_t mapNameAddr = mapNameBasePtr + 0xF8;
        // LogToFile("ReadMemoryValuesToJson: Reading mapName value..."); // Removed pre-read log
        if (SafeReadProcessMemory(reinterpret_cast<LPCVOID>(mapNameAddr), mapNameBuffer.data(), mapNameBufferSize, &bytesRead)) {
             // LogToFile("ReadMemoryValuesToJson: Trimming mapName data..."); // Removed trim log
             mapName = TrimStringData(mapNameBuffer.data(), bytesRead);
             // LogToFile(std::string("ReadMemoryValuesToJson: Trimmed mapName: '").append(mapName.substr(0, 50)).append("'")); // Removed trim log
             //if (mapName.empty() && bytesRead > 0) {
             //    LogToFile("ReadMemoryValuesToJson: mapName trimmed to empty string despite reading bytes."); // Removed trim log
             //}
             // LogToFile("ReadMemoryValuesToJson: Successfully processed mapName."); // Removed success log
        } else {
             success = false;
             errorMessage = "Failed to read mapName value";
             LogToFile("ReadMemoryValuesToJson: Failed to read mapName value."); // Keep failure log
        }
    } else if (success) {
        success = false;
        errorMessage = "Failed to read mapName base pointer";
        LogToFile("ReadMemoryValuesToJson: Failed to read mapName base pointer."); // Keep failure log
    }


    // --- Read characterName (12 chars * 2 bytes/char = 24 bytes) ---
    const size_t charNameBufferSize = 24;
    std::vector<char> charNameBuffer(charNameBufferSize);
    uintptr_t charNamePtrAddr = baseAddress + 0x001A2DA4;
    uintptr_t charNameAddr = 0;
    // LogToFile("ReadMemoryValuesToJson: Reading characterName pointer..."); // Removed pre-read log
    if (success && SafeReadProcessMemory(reinterpret_cast<LPCVOID>(charNamePtrAddr), &charNameAddr, sizeof(charNameAddr), &bytesRead) && bytesRead == sizeof(charNameAddr)) {
        // LogToFile("ReadMemoryValuesToJson: Reading characterName value..."); // Removed pre-read log
         if (SafeReadProcessMemory(reinterpret_cast<LPCVOID>(charNameAddr), charNameBuffer.data(), charNameBufferSize, &bytesRead)) {
            // LogToFile("ReadMemoryValuesToJson: Trimming characterName data..."); // Removed trim log
            characterName = TrimStringData(charNameBuffer.data(), bytesRead);
             // LogToFile(std::string("ReadMemoryValuesToJson: Trimmed characterName: '").append(characterName.substr(0, 50)).append("'")); // Removed trim log
             //if (characterName.empty() && bytesRead > 0) {
             //   LogToFile("ReadMemoryValuesToJson: characterName trimmed to empty string despite reading bytes."); // Removed trim log
             //}
            // LogToFile("ReadMemoryValuesToJson: Successfully processed characterName."); // Removed success log
        } else {
            success = false;
            errorMessage = "Failed to read characterName value";
            LogToFile("ReadMemoryValuesToJson: Failed to read characterName value."); // Keep failure log
        }
    } else if (success) {
        success = false;
        errorMessage = "Failed to read characterName pointer";
        LogToFile("ReadMemoryValuesToJson: Failed to read characterName pointer."); // Keep failure log
    }


    // --- Construct JSON ---
    // LogToFile("ReadMemoryValuesToJson: Constructing JSON response..."); // Removed construction log
    std::ostringstream jsonStream;
    if (success) {
        // Basic JSON construction without escaping (as per plan)
        // Note: If mapName or characterName contain quotes or backslashes, the JSON will be invalid.
        jsonStream << R"({"success": true, "data": {)";
        jsonStream << R"("x": )" << x << ", ";
        jsonStream << R"("y": )" << y << ", ";
        jsonStream << R"("mapId": )" << mapId << ", "; // Added new mapId (integer)
        jsonStream << R"("mapName": ")" << mapName << R"(", )"; // Renamed from mapId
        jsonStream << R"("characterName": ")" << characterName << R"(")";
        jsonStream << R"(}})";
        std::string resultJson = jsonStream.str();
        // LogToFile(std::string("ReadMemoryValuesToJson: Returning success JSON: ").append(resultJson.substr(0, 200))); // Removed old log
        // LogToFile("ReadMemoryValuesToJson: Successfully constructed success JSON."); // Removed old log
        LogToFile(resultJson); // Log the final JSON on success
        return resultJson;

    } else {
        // Basic JSON construction for error message (no escaping)
        jsonStream << R"({"success": false, "error": ")" << errorMessage << R"("})";
         std::string errorJson = jsonStream.str();
         // LogToFile(std::string("ReadMemoryValuesToJson: Returning error JSON: ").append(errorJson)); // Removed error JSON log
         // The specific error was already logged when it occurred.
        return errorJson;
    }
} 