#define _WIN32_WINNT 0x0600 // target Vista or later for full IPHLPAPI support
#include <winsock2.h>
#define WIN32_LEAN_AND_MEAN // Exclude rarely-used stuff from Windows headers
#include <windows.h>
#include <iphlpapi.h>
#include <iptypes.h> // Include this for IP_ADAPTER_ADDRESSES and related types
#include <stdlib.h> // For general utility functions if needed

#include <string>
#include <thread>
#include <chrono>
#include <atomic>
#include <vector>
#include <mutex> // Include mutex for thread safety later
#include <fstream> // For file logging
#include <sstream> // For formatting log messages
#include <iomanip> // For std::put_time
#include <ctime>   // For std::time, std::localtime

#include "memreader.h" // Include the header for memory reading functions

#pragma comment(lib, "ws2_32.lib") // Link winsock library

// --- Logging Setup ---
std::ofstream logFile;
std::mutex logMutex;
const char* logFileName = "memoryreadingdll_log.txt";
std::string logFilePath = ""; // Store the full path

// Control whether the JSON data itself is logged each second
static const bool logJsonData = false;

void LogToFile(const std::string& message) {
    std::lock_guard<std::mutex> lock(logMutex);
    if (logFile.is_open()) {
        auto now = std::chrono::system_clock::now();
        auto now_c = std::chrono::system_clock::to_time_t(now);
        std::tm now_tm;
        localtime_s(&now_tm, &now_c); // Use safer localtime_s on Windows

        // Get milliseconds
        auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch()) % 1000;

        logFile << std::put_time(&now_tm, "%Y-%m-%d %H:%M:%S")
                << '.' << std::setfill('0') << std::setw(3) << ms.count()
                << ": " << message << std::endl;
        // No need to explicitly flush after every message with std::endl
    }
}
// --- End Logging Setup ---

// Forward declarations
void UnloadOriginalDll();
bool LoadOriginalDll();

// Typedef for the original GetAdaptersAddresses function
typedef ULONG(WINAPI* PGETADAPTERSADDRESSES)(ULONG, ULONG, PVOID, PIP_ADAPTER_ADDRESSES, PULONG);

// Global variables
HMODULE hOriginalIphlpapi = NULL;
PGETADAPTERSADDRESSES RealGetAdaptersAddresses = NULL;
HANDLE hMapFile = NULL;
LPVOID pBuf = NULL;
const char* szMapFileName = "NexusTKMemoryData";
const SIZE_T MMF_SIZE = 1024; // 1KB Max size
std::atomic<bool> keepRunning(true);
std::thread memoryPollingThread;
std::string latestJsonData = R"({"success": false, "error": "Initializing..."})";
std::mutex dataMutex; // Mutex to protect access to latestJsonData

// Function to unload the original DLL
void UnloadOriginalDll() {
    if (hOriginalIphlpapi) {
        LogToFile("Unloading original IPHLPAPI.DLL.");
        FreeLibrary(hOriginalIphlpapi);
        hOriginalIphlpapi = NULL;
        RealGetAdaptersAddresses = NULL;
    } else {
        LogToFile("Attempted to unload original DLL, but it was not loaded.");
    }
}

// Function to load the original IPHLPAPI.DLL and get the function pointer
bool LoadOriginalDll() {
    LogToFile("Attempting to load original IPHLPAPI.DLL...");
    
    // try system32 first
    char systemPath[MAX_PATH];
    GetSystemDirectoryA(systemPath, MAX_PATH);
    strcat_s(systemPath, "\\IPHLPAPI.DLL");
    LogToFile(std::string("System path for DLL: ") + systemPath);

    hOriginalIphlpapi = LoadLibraryA(systemPath);
    
    // if system32 fails, try current directory as fallback
    if (!hOriginalIphlpapi) {
        LogToFile("Failed to load from system32, trying current directory...");
        hOriginalIphlpapi = LoadLibraryA("IPHLPAPI.DLL");
    }

    if (!hOriginalIphlpapi) {
        DWORD error = GetLastError();
        std::ostringstream oss;
        oss << "Failed to load original IPHLPAPI.DLL. Error code: " << error;
        LogToFile(oss.str());
        return false;
    }
    LogToFile("Original IPHLPAPI.DLL loaded successfully.");

    // get function pointer with error handling
    RealGetAdaptersAddresses = (PGETADAPTERSADDRESSES)GetProcAddress(hOriginalIphlpapi, "GetAdaptersAddresses");
    if (!RealGetAdaptersAddresses) {
        DWORD error = GetLastError();
        std::ostringstream oss;
        oss << "Failed to get GetProcAddress for GetAdaptersAddresses. Error code: " << error;
        LogToFile(oss.str());
        UnloadOriginalDll(); // cleanup on failure
        return false;
    }
    LogToFile("Successfully obtained GetAdaptersAddresses function pointer.");
    return true;
}

// Background thread function for polling memory
void MemoryPollingLoop() {
    LogToFile("Memory polling thread started.");
    while (keepRunning) {
        std::string jsonData = ReadMemoryValuesToJson(); // This function will be in memreader.cpp

        // Conditionally log the JSON data before writing to MMF
        if (logJsonData) {
            LogToFile(std::string("JSON Data: ") + jsonData);
        }

        // Update the shared data and write to MMF
        {
            std::lock_guard<std::mutex> lock(dataMutex);
            latestJsonData = jsonData;
            if (pBuf) {
                ZeroMemory(pBuf, MMF_SIZE); // Clear previous data
                // Check if jsonData fits, including null terminator
                if (latestJsonData.length() < MMF_SIZE) {
                    strncpy_s(static_cast<char*>(pBuf), MMF_SIZE, latestJsonData.c_str(), latestJsonData.length());
                } else {
                    // Data too large, truncate. Log this?
                    strncpy_s(static_cast<char*>(pBuf), MMF_SIZE, latestJsonData.c_str(), MMF_SIZE - 1);
                    LogToFile("Warning: JSON data truncated to fit MMF size.");
                }
            } else {
                 LogToFile("Error: Attempted to write to MMF, but pBuf is NULL.");
            }
        }

        // Wait for 1 second
        std::this_thread::sleep_for(std::chrono::seconds(1));
    }
     LogToFile("Memory polling thread stopping.");
}

// Setup Memory Mapped File
bool SetupMMF() {
    LogToFile("Attempting to setup Memory Mapped File...");
    LogToFile(std::string("MMF Name: ") + szMapFileName);
    LogToFile(std::string("MMF Size: ") + std::to_string(MMF_SIZE));

    hMapFile = CreateFileMappingA(
        INVALID_HANDLE_VALUE,    // Use paging file
        NULL,                    // Default security
        PAGE_READWRITE,          // Read/write access
        0,                       // Maximum object size (high-order DWORD)
        MMF_SIZE,                // Maximum object size (low-order DWORD)
        szMapFileName);          // Name of mapping object

    if (hMapFile == NULL) {
        DWORD error = GetLastError();
        std::ostringstream oss;
        oss << "Failed to create file mapping object. Error code: " << error;
        LogToFile(oss.str());
        return false;
    }
    LogToFile("CreateFileMappingA succeeded.");

    pBuf = MapViewOfFile(
        hMapFile,                // Handle to map object
        FILE_MAP_ALL_ACCESS,     // Read/write permission
        0,
        0,
        MMF_SIZE);

    if (pBuf == NULL) {
        DWORD error = GetLastError();
        std::ostringstream oss;
        oss << "Failed to map view of file. Error code: " << error;
        LogToFile(oss.str());
        CloseHandle(hMapFile);
        hMapFile = NULL;
        return false;
    }
    LogToFile("MapViewOfFile succeeded.");

    // Initial write to MMF
     LogToFile("Performing initial write to MMF...");
     {
        std::lock_guard<std::mutex> lock(dataMutex);
        if (pBuf) { // Double check pBuf just in case
             ZeroMemory(pBuf, MMF_SIZE); // Clear previous data
             if (latestJsonData.length() < MMF_SIZE) {
                 strncpy_s(static_cast<char*>(pBuf), MMF_SIZE, latestJsonData.c_str(), latestJsonData.length());
             } else {
                  strncpy_s(static_cast<char*>(pBuf), MMF_SIZE, latestJsonData.c_str(), MMF_SIZE - 1);
             }
             LogToFile("Initial write completed.");
        } else {
             LogToFile("Error: pBuf became NULL before initial write.");
             // This case should theoretically not happen if MapViewOfFile succeeded
             // but indicates a potential logic error if it does.
             // We might still want to return true here as the mapping exists,
             // but logging is critical. For now, let's consider this setup successful
             // but log the write failure.
        }
     }

    LogToFile("MMF setup completed successfully.");
    return true;
}

// Cleanup Memory Mapped File
void CleanupMMF() {
    LogToFile("Cleaning up MMF...");
    if (pBuf) {
        LogToFile("Unmapping view of file.");
        UnmapViewOfFile(pBuf);
        pBuf = NULL;
    } else {
        LogToFile("MMF view was already unmapped or never mapped.");
    }
    if (hMapFile) {
        LogToFile("Closing file mapping handle.");
        CloseHandle(hMapFile);
        hMapFile = NULL;
    } else {
         LogToFile("MMF handle was already closed or never created.");
    }
     LogToFile("MMF cleanup finished.");
}


// --- DLL Entry Point ---
BOOL APIENTRY DllMain(HMODULE hModule,
                      DWORD  ul_reason_for_call,
                      LPVOID lpReserved) {
    switch (ul_reason_for_call) {
        case DLL_PROCESS_ATTACH:
            // Open log file here, before any LogToFile calls
             {
                 std::lock_guard<std::mutex> lock(logMutex);
                 // Open in append mode, creating if it doesn't exist
                 logFile.open(logFileName, std::ios::app);
                 if (!logFile.is_open()) {
                    // Optionally use OutputDebugStringA here if file logging fails?
                 }
             }
            LogToFile("--- DLL_PROCESS_ATTACH ---");
            DisableThreadLibraryCalls(hModule); // Optimization

            if (!LoadOriginalDll()) {
                 LogToFile("LoadOriginalDll failed. Detaching.");
                 // Clean up log file before returning FALSE
                 {
                     std::lock_guard<std::mutex> lock(logMutex);
                     if (logFile.is_open()) {
                         logFile.close();
                     }
                 }
                return FALSE; // Prevent DLL from loading further
            }

            if (!SetupMMF()) {
                 LogToFile("SetupMMF failed. Unloading original DLL and detaching.");
                 UnloadOriginalDll(); // Clean up previously loaded DLL
                 // Clean up log file before returning FALSE
                 {
                     std::lock_guard<std::mutex> lock(logMutex);
                     if (logFile.is_open()) {
                         logFile.close();
                     }
                 }
                 return FALSE; // Failed to set up MMF
            }

            // Start the background polling thread
            LogToFile("Starting background memory polling thread...");
            keepRunning = true;
            memoryPollingThread = std::thread(MemoryPollingLoop);
            LogToFile("DLL_PROCESS_ATTACH finished successfully.");
            break;

        case DLL_THREAD_ATTACH:
            // LogToFile("--- DLL_THREAD_ATTACH ---"); // Usually not needed/noisy
            break;

        case DLL_THREAD_DETACH:
             // LogToFile("--- DLL_THREAD_DETACH ---"); // Usually not needed/noisy
            break;

        case DLL_PROCESS_DETACH:
            LogToFile("--- DLL_PROCESS_DETACH ---");
            // Signal the thread to stop and wait for it to finish
             LogToFile("Signalling polling thread to stop...");
            keepRunning = false;
            if (memoryPollingThread.joinable()) {
                 LogToFile("Waiting for polling thread to join...");
                memoryPollingThread.join();
                 LogToFile("Polling thread joined.");
            } else {
                 LogToFile("Polling thread was not joinable.");
            }
            // Cleanup
            CleanupMMF();
            UnloadOriginalDll();
             LogToFile("DLL_PROCESS_DETACH finished.");
             // Close log file handle
             {
                 std::lock_guard<std::mutex> lock(logMutex);
                 if (logFile.is_open()) {
                     logFile.close();
                 }
             }
            break;
    }
    return TRUE;
}

// --- Exported Proxy Function ---

// Use extern "C" to prevent C++ name mangling for exported functions
extern "C" {
    __declspec(dllexport) ULONG WINAPI GetAdaptersAddresses(ULONG Family, ULONG Flags, PVOID Reserved, PIP_ADAPTER_ADDRESSES AdapterAddresses, PULONG SizePointer) {
        // Optional: Log the proxy call? Could be very noisy.
        // LogToFile("GetAdaptersAddresses called.");
        if (!RealGetAdaptersAddresses) {
             LogToFile("Error: GetAdaptersAddresses called, but real function pointer is NULL!");
             // Maybe try loading it again? Or return an error code?
             return ERROR_DLL_INIT_FAILED; // Or another appropriate error code
        }
        // Call the original function
        return RealGetAdaptersAddresses(Family, Flags, Reserved, AdapterAddresses, SizePointer);
    }
} 