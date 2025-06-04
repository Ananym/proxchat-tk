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
#include "ZeroMQPublisher.h"

// --- logging setup ---
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

// global variables for zeromq
std::unique_ptr<ZeroMQPublisher> zmqPublisher;
const std::string ZMQ_ENDPOINT = "ipc://proxchat";

std::atomic<bool> keepRunning(true);
std::thread memoryPollingThread;

// background thread function for polling memory
void MemoryPollingLoop() {
    LogToFile("Memory polling thread started.");
    int messageCount = 0;
    int successCount = 0;
    
    while (keepRunning) {
        // create structured message
        GameDataMessage gameMsg = CreateGameDataMessage();
        
        // publish via zeromq - no need to check for connected clients
        if (zmqPublisher && zmqPublisher->IsRunning()) {
            bool success = zmqPublisher->PublishMessage(gameMsg);
            messageCount++;
            if (success) successCount++;
            
            // log every 50 attempts to avoid spam
            if (messageCount % 50 == 0) {
                LogToFile("Published " + std::to_string(messageCount) + " messages, " + 
                         std::to_string(successCount) + " successful");
            }
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    LogToFile("Memory polling thread stopping. Total: " + std::to_string(messageCount) + 
             " messages, " + std::to_string(successCount) + " successful");
}

// setup zeromq publisher
bool SetupZeroMQ() {
    LogToFile("Setting up ZeroMQ publisher...");
    LogToFile("ZMQ endpoint: " + ZMQ_ENDPOINT);
    
    try {
        zmqPublisher = std::make_unique<ZeroMQPublisher>(ZMQ_ENDPOINT);
        if (!zmqPublisher->Start()) {
            LogToFile("Failed to start ZeroMQ publisher");
        return false;
    }
        LogToFile("ZeroMQ publisher started successfully");
        
        // small delay to allow subscribers to connect
        std::this_thread::sleep_for(std::chrono::milliseconds(500));
        LogToFile("Ready to send messages after subscriber connect delay");
        
        return true;
    } catch (const std::exception& e) {
        LogToFile("Exception setting up ZeroMQ: " + std::string(e.what()));
        return false;
    }
}

// cleanup zeromq
void CleanupZeroMQ() {
    LogToFile("Cleaning up ZeroMQ...");
    if (zmqPublisher) {
        zmqPublisher->Stop();
        zmqPublisher.reset();
    }
    LogToFile("ZeroMQ cleanup finished.");
}

// --- dll entry point ---
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

            if (!SetupZeroMQ()) {
                LogToFile("SetupZeroMQ failed. Detaching.");
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
            CleanupZeroMQ();
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