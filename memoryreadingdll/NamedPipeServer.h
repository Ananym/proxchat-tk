#pragma once
#include <windows.h>
#include <string>
#include <atomic>
#include <mutex>
#include "GameDataMessage.h"

class NamedPipeServer {
public:
    NamedPipeServer(const std::string& pipeName);
    ~NamedPipeServer();

    bool Start();
    void Stop();
    bool SendMessage(const GameDataMessage& message);
    bool IsClientConnected() const { return _clientConnected.load(); }

private:
    void ServerThread();
    bool CreatePipeInstance();
    void HandleClientConnection();
    void CleanupPipe();

    std::string _pipeName;
    HANDLE _hPipe;
    std::atomic<bool> _running;
    std::atomic<bool> _clientConnected;
    std::thread _serverThread;
    std::mutex _writeMutex;
    uint32_t _sequenceNumber;
    
    static constexpr DWORD PIPE_BUFFER_SIZE = 1024;
    static constexpr DWORD PIPE_TIMEOUT_MS = 5000;
}; 