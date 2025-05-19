#define _WIN32_WINNT 0x0600
#include <windows.h>
#include <string>
#include <thread>
#include <chrono>
#include <atomic>
#include <vector>
#include <mutex>
#include <fstream>
#include <sstream>
#include <iomanip>
#include <ctime>

#include "memreader.h"

// --- Logging Setup ---
std::ofstream logFile;
std::mutex logMutex;
const char* logFileName = "memoryreadingdll_log.txt";

// Control whether the JSON data itself is logged each second
static const bool logJsonData = false;

void LogToFile(const std::string& message) {
    std::lock_guard<std::mutex> lock(logMutex);
    if (logFile.is_open()) {
        auto now = std::chrono::system_clock::now();
        auto now_c = std::chrono::system_clock::to_time_t(now);
        std::tm now_tm;
        localtime_s(&now_tm, &now_c);

        auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch()) % 1000;

        logFile << std::put_time(&now_tm, "%Y-%m-%d %H:%M:%S")
                << '.' << std::setfill('0') << std::setw(3) << ms.count()
                << ": " << message << std::endl;
    }
}

// Global variables
HANDLE hMapFile = NULL;
LPVOID pBuf = NULL;
const char* szMapFileName = "NexusTKMemoryData";
const SIZE_T MMF_SIZE = 1024; // 1KB Max size
std::atomic<bool> keepRunning(true);
std::thread memoryPollingThread;
std::string latestJsonData = R"({"success": false, "error": "Initializing..."})";
std::mutex dataMutex;

// Background thread function for polling memory
void MemoryPollingLoop() {
    LogToFile("Memory polling thread started.");
    while (keepRunning) {
        std::string jsonData = ReadMemoryValuesToJson();

        if (logJsonData) {
            LogToFile(std::string("JSON Data: ") + jsonData);
        }

        {
            std::lock_guard<std::mutex> lock(dataMutex);
            latestJsonData = jsonData;
            if (pBuf) {
                ZeroMemory(pBuf, MMF_SIZE);
                if (latestJsonData.length() < MMF_SIZE) {
                    strncpy_s(static_cast<char*>(pBuf), MMF_SIZE, latestJsonData.c_str(), latestJsonData.length());
                } else {
                    strncpy_s(static_cast<char*>(pBuf), MMF_SIZE, latestJsonData.c_str(), MMF_SIZE - 1);
                    LogToFile("Warning: JSON data truncated to fit MMF size.");
                }
            } else {
                LogToFile("Error: Attempted to write to MMF, but pBuf is NULL.");
            }
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    LogToFile("Memory polling thread stopping.");
}

// Setup Memory Mapped File
bool SetupMMF() {
    LogToFile("Attempting to setup Memory Mapped File...");
    LogToFile(std::string("MMF Name: ") + szMapFileName);
    LogToFile(std::string("MMF Size: ") + std::to_string(MMF_SIZE));

    hMapFile = CreateFileMappingA(
        INVALID_HANDLE_VALUE,
        NULL,
        PAGE_READWRITE,
        0,
        MMF_SIZE,
        szMapFileName);

    if (hMapFile == NULL) {
        DWORD error = GetLastError();
        std::ostringstream oss;
        oss << "Failed to create file mapping object. Error code: " << error;
        LogToFile(oss.str());
        return false;
    }
    LogToFile("CreateFileMappingA succeeded.");

    pBuf = MapViewOfFile(
        hMapFile,
        FILE_MAP_ALL_ACCESS,
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

    LogToFile("Performing initial write to MMF...");
    {
        std::lock_guard<std::mutex> lock(dataMutex);
        if (pBuf) {
            ZeroMemory(pBuf, MMF_SIZE);
            if (latestJsonData.length() < MMF_SIZE) {
                strncpy_s(static_cast<char*>(pBuf), MMF_SIZE, latestJsonData.c_str(), latestJsonData.length());
            } else {
                strncpy_s(static_cast<char*>(pBuf), MMF_SIZE, latestJsonData.c_str(), MMF_SIZE - 1);
            }
            LogToFile("Initial write completed.");
        } else {
            LogToFile("Error: pBuf became NULL before initial write.");
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
    }
    if (hMapFile) {
        LogToFile("Closing file mapping handle.");
        CloseHandle(hMapFile);
        hMapFile = NULL;
    }
    LogToFile("MMF cleanup finished.");
}

// --- DLL Entry Point ---
BOOL APIENTRY DllMain(HMODULE hModule,
                      DWORD  ul_reason_for_call,
                      LPVOID lpReserved) {
    switch (ul_reason_for_call) {
        case DLL_PROCESS_ATTACH:
            {
                std::lock_guard<std::mutex> lock(logMutex);
                logFile.open(logFileName, std::ios::app);
            }
            LogToFile("--- DLL_PROCESS_ATTACH ---");
            DisableThreadLibraryCalls(hModule);

            if (!SetupMMF()) {
                LogToFile("SetupMMF failed. Detaching.");
                {
                    std::lock_guard<std::mutex> lock(logMutex);
                    if (logFile.is_open()) {
                        logFile.close();
                    }
                }
                return FALSE;
            }

            LogToFile("Starting background memory polling thread...");
            keepRunning = true;
            memoryPollingThread = std::thread(MemoryPollingLoop);
            LogToFile("DLL_PROCESS_ATTACH finished successfully.");
            break;

        case DLL_PROCESS_DETACH:
            LogToFile("--- DLL_PROCESS_DETACH ---");
            LogToFile("Signalling polling thread to stop...");
            keepRunning = false;
            if (memoryPollingThread.joinable()) {
                LogToFile("Waiting for polling thread to join...");
                memoryPollingThread.join();
                LogToFile("Polling thread joined.");
            }
            CleanupMMF();
            LogToFile("DLL_PROCESS_DETACH finished.");
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