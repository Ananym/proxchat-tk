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

// --- logging setup ---
std::ofstream logFile;
std::mutex logMutex;
const char* logFileName = "memoryreadingdll_log.txt";
std::atomic<bool> debugLoggingEnabled(false);

bool IsDebugFlagPresent() {
    std::string cmdLine = GetCommandLineA();
    return cmdLine.find("--debug") != std::string::npos;
}

void LogToFile(const std::string& message) {
    if (!debugLoggingEnabled) return;
    
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

// named pipe communication
const std::string PIPE_NAME = "\\\\.\\pipe\\proxchattk";
const DWORD PIPE_BUFFER_SIZE = 1024;
const DWORD HEARTBEAT_INTERVAL_MS = 3000;

#pragma pack(push, 1)
struct PipeMessage {
    uint32_t messageType;  // 0=heartbeat, 1=game_data
    uint32_t dataSize;
    uint8_t data[64];      // max size for game data message
};
#pragma pack(pop)

class NamedPipeServer {
private:
    HANDLE pipe;
    std::atomic<bool> running;
    std::atomic<bool> connected;
    std::thread pipeThread;
    std::thread heartbeatThread;
    std::chrono::steady_clock::time_point lastHeartbeatReceived;
    std::mutex pipeMutex;

    void CreatePipe() {
        pipe = CreateNamedPipeA(
            PIPE_NAME.c_str(),
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
            2,  // increased from 1 to allow better reconnection handling
            PIPE_BUFFER_SIZE,
            PIPE_BUFFER_SIZE,
            0,
            nullptr
        );

        if (pipe == INVALID_HANDLE_VALUE) {
            DWORD error = GetLastError();
            LogToFile("Failed to create named pipe: " + std::to_string(error));
            
            // if pipe is busy, wait a bit and retry once
            if (error == ERROR_PIPE_BUSY) {
                LogToFile("Pipe busy, waiting 100ms and retrying...");
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
                pipe = CreateNamedPipeA(
                    PIPE_NAME.c_str(),
                    PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                    PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                    2,
                    PIPE_BUFFER_SIZE,
                    PIPE_BUFFER_SIZE,
                    0,
                    nullptr
                );
                if (pipe != INVALID_HANDLE_VALUE) {
                    LogToFile("Named pipe created successfully on retry");
                } else {
                    LogToFile("Named pipe creation failed again: " + std::to_string(GetLastError()));
                }
            }
        } else {
            LogToFile("Named pipe created successfully");
        }
    }

    void PipeThreadFunction() {
        LogToFile("Pipe thread started");
        
        while (running) {
            if (pipe == INVALID_HANDLE_VALUE) {
                CreatePipe();
                if (pipe == INVALID_HANDLE_VALUE) {
                    std::this_thread::sleep_for(std::chrono::milliseconds(1000));
                    continue;
                }
            }

            LogToFile("Waiting for client connection...");
            OVERLAPPED connectOverlapped = {0};
            connectOverlapped.hEvent = CreateEvent(nullptr, TRUE, FALSE, nullptr);
            
            BOOL connectResult = ConnectNamedPipe(pipe, &connectOverlapped);
            DWORD connectError = GetLastError();
            
            if (!connectResult && connectError == ERROR_IO_PENDING) {
                DWORD waitResult = WaitForSingleObject(connectOverlapped.hEvent, 1000);
                if (waitResult != WAIT_OBJECT_0) {
                    CloseHandle(connectOverlapped.hEvent);
                    continue;
                }
            } else if (!connectResult && connectError != ERROR_PIPE_CONNECTED) {
                LogToFile("ConnectNamedPipe failed: " + std::to_string(connectError));
                CloseHandle(connectOverlapped.hEvent);
                CleanupPipe();
                std::this_thread::sleep_for(std::chrono::milliseconds(1000));
                continue;
            }
            
            CloseHandle(connectOverlapped.hEvent);
            connected = true;
            lastHeartbeatReceived = std::chrono::steady_clock::now();
            LogToFile("Client connected to named pipe");

            HandleClientCommunication();
        }
        
        LogToFile("Pipe thread stopping");
    }

    void HandleClientCommunication() {
        PipeMessage incomingMsg;
        DWORD bytesRead;
        OVERLAPPED readOverlapped = {0};
        readOverlapped.hEvent = CreateEvent(nullptr, TRUE, FALSE, nullptr);

        while (running && connected) {
            BOOL readResult = ReadFile(pipe, &incomingMsg, sizeof(PipeMessage), &bytesRead, &readOverlapped);
            DWORD readError = GetLastError();

            if (!readResult && readError == ERROR_IO_PENDING) {
                DWORD waitResult = WaitForSingleObject(readOverlapped.hEvent, 500);
                if (waitResult == WAIT_OBJECT_0) {
                    GetOverlappedResult(pipe, &readOverlapped, &bytesRead, FALSE);
                } else {
                    CancelIo(pipe);
                    continue;
                }
            } else if (!readResult) {
                LogToFile("ReadFile failed: " + std::to_string(readError));
                break;
            }

            if (bytesRead == sizeof(PipeMessage) && incomingMsg.messageType == 0) {
                lastHeartbeatReceived = std::chrono::steady_clock::now();
                SendHeartbeatResponse();
            }
        }

        CloseHandle(readOverlapped.hEvent);
        DisconnectClient();
    }

    void SendHeartbeatResponse() {
        std::lock_guard<std::mutex> lock(pipeMutex);
        if (!connected || pipe == INVALID_HANDLE_VALUE) return;

        PipeMessage heartbeatMsg = {0};
        heartbeatMsg.messageType = 0;
        heartbeatMsg.dataSize = 0;

        DWORD bytesWritten;
        OVERLAPPED writeOverlapped = {0};
        writeOverlapped.hEvent = CreateEvent(nullptr, TRUE, FALSE, nullptr);

        BOOL result = WriteFile(pipe, &heartbeatMsg, sizeof(PipeMessage), &bytesWritten, &writeOverlapped);
        if (!result && GetLastError() == ERROR_IO_PENDING) {
            WaitForSingleObject(writeOverlapped.hEvent, 1000);
            GetOverlappedResult(pipe, &writeOverlapped, &bytesWritten, FALSE);
        }

        CloseHandle(writeOverlapped.hEvent);
    }

    void HeartbeatThreadFunction() {
        LogToFile("Heartbeat monitor thread started");
        
        while (running) {
            std::this_thread::sleep_for(std::chrono::milliseconds(HEARTBEAT_INTERVAL_MS));
            
            if (connected) {
                auto now = std::chrono::steady_clock::now();
                auto timeSinceLastHeartbeat = std::chrono::duration_cast<std::chrono::milliseconds>(
                    now - lastHeartbeatReceived);
                
                if (timeSinceLastHeartbeat.count() > HEARTBEAT_INTERVAL_MS * 2) {
                    LogToFile("Heartbeat timeout - disconnecting client");
                    DisconnectClient();
                }
            }
        }
        
        LogToFile("Heartbeat monitor thread stopping");
    }

    void DisconnectClient() {
        if (connected) {
            LogToFile("Disconnecting client");
            connected = false;
            DisconnectNamedPipe(pipe);
        }
    }

    void CleanupPipe() {
        if (pipe != INVALID_HANDLE_VALUE) {
            CloseHandle(pipe);
            pipe = INVALID_HANDLE_VALUE;
        }
    }

public:
    NamedPipeServer() : pipe(INVALID_HANDLE_VALUE), running(false), connected(false) {}

    bool Start() {
        LogToFile("Starting named pipe server...");
        running = true;
        
        pipeThread = std::thread(&NamedPipeServer::PipeThreadFunction, this);
        heartbeatThread = std::thread(&NamedPipeServer::HeartbeatThreadFunction, this);
        
        return true;
    }

    void Stop() {
        LogToFile("Stopping named pipe server...");
        running = false;
        DisconnectClient();
        
        if (pipeThread.joinable()) {
            pipeThread.join();
        }
        if (heartbeatThread.joinable()) {
            heartbeatThread.join();
        }
        
        CleanupPipe();
        LogToFile("Named pipe server stopped");
    }

    bool SendGameData(const GameDataMessage& gameMsg) {
        std::lock_guard<std::mutex> lock(pipeMutex);
        if (!connected || pipe == INVALID_HANDLE_VALUE) {
            return false;
        }

        PipeMessage pipeMsg = {0};
        pipeMsg.messageType = 1;
        pipeMsg.dataSize = sizeof(GameDataMessage);
        memcpy(pipeMsg.data, &gameMsg, sizeof(GameDataMessage));

        DWORD bytesWritten;
        OVERLAPPED writeOverlapped = {0};
        writeOverlapped.hEvent = CreateEvent(nullptr, TRUE, FALSE, nullptr);

        BOOL result = WriteFile(pipe, &pipeMsg, sizeof(PipeMessage), &bytesWritten, &writeOverlapped);
        DWORD error = GetLastError();
        
        if (!result && error == ERROR_IO_PENDING) {
            DWORD waitResult = WaitForSingleObject(writeOverlapped.hEvent, 1000);
            if (waitResult == WAIT_OBJECT_0) {
                GetOverlappedResult(pipe, &writeOverlapped, &bytesWritten, FALSE);
                result = TRUE;
            }
        }

        CloseHandle(writeOverlapped.hEvent);
        
        if (!result) {
            LogToFile("Failed to send game data: " + std::to_string(GetLastError()));
            DisconnectClient();
            return false;
        }

        return bytesWritten == sizeof(PipeMessage);
    }

    bool IsConnected() const {
        return connected;
    }
};

// global variables
std::unique_ptr<NamedPipeServer> pipeServer;
std::atomic<bool> keepRunning(true);
std::thread memoryPollingThread;

void MemoryPollingLoop() {
    LogToFile("Memory polling thread started.");
    int messageCount = 0;
    int successCount = 0;
    
    while (keepRunning) {
        GameDataMessage gameMsg = CreateGameDataMessage();
        
        if (pipeServer && pipeServer->IsConnected()) {
            bool success = pipeServer->SendGameData(gameMsg);
            messageCount++;
            if (success) successCount++;
            
            // log first few successful messages with success flag info
            static int successfulSendsLogged = 0;
            if (success && (gameMsg.flags & 0x01) && successfulSendsLogged < 3) {
                successfulSendsLogged++;
                LogToFile("SENT SUCCESS MESSAGE #" + std::to_string(successfulSendsLogged) + ": Flags=0x" + std::to_string(gameMsg.flags));
            }
            
            // log every 50 attempts to avoid spam
            if (messageCount % 50 == 0) {
                LogToFile("Sent " + std::to_string(messageCount) + " messages, " + 
                         std::to_string(successCount) + " successful");
            }
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    LogToFile("Memory polling thread stopping. Total: " + std::to_string(messageCount) + 
             " messages, " + std::to_string(successCount) + " successful");
}

bool SetupNamedPipe() {
    LogToFile("Setting up named pipe server...");
    
    try {
        pipeServer = std::make_unique<NamedPipeServer>();
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

void CleanupNamedPipe() {
    LogToFile("Cleaning up named pipe...");
    if (pipeServer) {
        pipeServer->Stop();
        pipeServer.reset();
    }
    LogToFile("Named pipe cleanup finished.");
}

// --- dll entry point ---
BOOL APIENTRY DllMain(HMODULE hModule,
                      DWORD  ul_reason_for_call,
                      LPVOID lpReserved) {
    switch (ul_reason_for_call) {
        case DLL_PROCESS_ATTACH:
            debugLoggingEnabled = IsDebugFlagPresent();
            
            if (debugLoggingEnabled) {
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
            if (debugLoggingEnabled) {
                std::lock_guard<std::mutex> lock(logMutex);
                if (logFile.is_open()) {
                    logFile.close();
                }
            }
            break;
    }
    return TRUE;
} 