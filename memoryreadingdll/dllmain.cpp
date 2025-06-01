#define _WIN32_WINNT 0x0600
#include <windows.h>
#include <string>
#include <thread>
#include <chrono>
#include <atomic>
#include <mutex>
#include <fstream>
#include <sstream>
#include <iomanip>
#include <ctime>

#include "memreader.h"
#include "NamedPipeServer.h"

// --- Logging Setup ---
std::ofstream logFile;
std::mutex logMutex;
const char* logFileName = "memoryreadingdll_log.txt";

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

// global variables for named pipe
std::unique_ptr<NamedPipeServer> pipeServer;
const std::string PIPE_NAME = "NexusTKGameData";

std::atomic<bool> keepRunning(true);
std::thread memoryPollingThread;

// background thread function for polling memory
void MemoryPollingLoop() {
    LogToFile("Memory polling thread started.");
    auto lastHeartbeat = std::chrono::steady_clock::now();
    const auto heartbeatInterval = std::chrono::seconds(5);  // send heartbeat every 5 seconds
    
    while (keepRunning) {
        // create structured message
        GameDataMessage gameMsg = CreateGameDataMessage();
        
        // send via named pipe if client connected
        if (pipeServer && pipeServer->IsClientConnected()) {
            bool messageSent = pipeServer->SendMessage(gameMsg);
            
            // if game data send failed or it's time for heartbeat, send a heartbeat
            auto now = std::chrono::steady_clock::now();
            if (!messageSent || (now - lastHeartbeat) >= heartbeatInterval) {
                // create heartbeat message
                GameDataMessage heartbeat = {};
                heartbeat.messageType = MSG_TYPE_HEARTBEAT;
                heartbeat.sequenceNumber = 0;  // heartbeats don't need sequence numbers
                heartbeat.timestampMs = static_cast<uint64_t>(
                    std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::system_clock::now().time_since_epoch()).count());
                heartbeat.flags = FLAG_SUCCESS;
                
                if (pipeServer->SendMessage(heartbeat)) {
                    lastHeartbeat = now;
                }
            }
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    LogToFile("Memory polling thread stopping.");
}

// setup named pipe server
bool SetupNamedPipe() {
    LogToFile("Setting up Named Pipe server...");
    LogToFile("Pipe name: " + PIPE_NAME);
    
    try {
        pipeServer = std::make_unique<NamedPipeServer>(PIPE_NAME);
        if (!pipeServer->Start()) {
            LogToFile("Failed to start named pipe server");
        return false;
    }
        LogToFile("Named pipe server started successfully");
        return true;
    } catch (const std::exception& e) {
        LogToFile("Exception setting up named pipe: " + std::string(e.what()));
        return false;
    }
}

// cleanup named pipe
void CleanupNamedPipe() {
    LogToFile("Cleaning up named pipe...");
    if (pipeServer) {
        pipeServer->Stop();
        pipeServer.reset();
    }
    LogToFile("Named pipe cleanup finished.");
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

            if (!SetupNamedPipe()) {
                LogToFile("SetupNamedPipe failed. Detaching.");
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
            CleanupNamedPipe();
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