#pragma once
#include <windows.h>
#include <string>
#include <atomic>
#include <mutex>
#include "GameDataMessage.h"

// active challenge-response handshake structures for phantom connection detection
#pragma pack(push, 1)
struct ConnectionHandshake {
    uint32_t magic;           // 0xDEADBEEF
    uint64_t timestamp;       // milliseconds since epoch  
    uint32_t connectionId;    // unique ID for this connection attempt
};

struct HandshakeResponse {
    uint32_t magic;           // 0xBEEFDEAD
    uint32_t connectionId;    // echo back the connection ID
};
#pragma pack(pop)

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
    GameDataMessage CreateHandshakeMessage();  // create handshake for connection verification

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