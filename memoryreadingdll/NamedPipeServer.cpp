#include "NamedPipeServer.h"
#include <thread>
#include <chrono>
#include <sddl.h>

// forward declaration from dllmain.cpp
extern void LogToFile(const std::string& message);

// create a security descriptor that allows access for the current user regardless of elevation
SECURITY_ATTRIBUTES* CreatePipeSecurityAttributes() {
    static SECURITY_ATTRIBUTES sa;
    static SECURITY_DESCRIPTOR sd;
    
    // create a security descriptor string that allows:
    // - Everyone (WD)
    // - SYSTEM account (SY) 
    // - Administrators group (BA)
    // D: = DACL, A = Allow, GA = Generic All
    LPCSTR securityDescriptorString = "D:(A;;GA;;;WD)(A;;GA;;;SY)(A;;GA;;;BA)";
    
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorA(
        securityDescriptorString,
        SDDL_REVISION_1,
        &sa.lpSecurityDescriptor,
        NULL)) {
        LogToFile("Failed to create security descriptor for named pipe");
        return nullptr;
    }
    
    sa.nLength = sizeof(SECURITY_ATTRIBUTES);
    sa.bInheritHandle = FALSE;
    
    LogToFile("Created permissive security descriptor for named pipe");
    return &sa;
}

NamedPipeServer::NamedPipeServer(const std::string& pipeName)
    : _pipeName(pipeName)
    , _hPipe(INVALID_HANDLE_VALUE)
    , _running(false)
    , _clientConnected(false)
    , _sequenceNumber(0)
{
}

NamedPipeServer::~NamedPipeServer() {
    Stop();
}

bool NamedPipeServer::Start() {
    if (_running.load()) {
        LogToFile("NamedPipeServer: Already running");
        return true;
    }

    LogToFile("NamedPipeServer: Starting server for pipe: " + _pipeName);
    _running = true;
    _serverThread = std::thread(&NamedPipeServer::ServerThread, this);
    
    return true;
}

void NamedPipeServer::Stop() {
    if (!_running.load()) return;

    LogToFile("NamedPipeServer: Stopping server");
    _running = false;
    
    // wake up any blocking operations
    if (_hPipe != INVALID_HANDLE_VALUE) {
        CancelIo(_hPipe);
    }
    
    if (_serverThread.joinable()) {
        _serverThread.join();
    }
    
    CleanupPipe();
    LogToFile("NamedPipeServer: Server stopped");
}

bool NamedPipeServer::SendMessage(const GameDataMessage& message) {
    if (!_clientConnected.load() || _hPipe == INVALID_HANDLE_VALUE) {
        return false;
    }

    std::lock_guard<std::mutex> lock(_writeMutex);
    
    DWORD bytesWritten = 0;
    BOOL success = WriteFile(
        _hPipe,
        &message,
        sizeof(GameDataMessage),
        &bytesWritten,
        nullptr
    );

    if (!success || bytesWritten != sizeof(GameDataMessage)) {
        DWORD error = GetLastError();
        if (error == ERROR_BROKEN_PIPE || error == ERROR_NO_DATA) {
            LogToFile("NamedPipeServer: Client disconnected during write");
            _clientConnected = false;
        } else {
            LogToFile("NamedPipeServer: Write failed, error: " + std::to_string(error));
        }
        return false;
    }

    return true;
}

void NamedPipeServer::ServerThread() {
    LogToFile("NamedPipeServer: Server thread started");
    
    while (_running.load()) {
        if (!CreatePipeInstance()) {
            LogToFile("NamedPipeServer: Failed to create pipe instance, retrying in 1s");
            std::this_thread::sleep_for(std::chrono::seconds(1));
            continue;
        }

        LogToFile("NamedPipeServer: Waiting for client connection...");
        
        // wait for client to connect
        BOOL connected = ConnectNamedPipe(_hPipe, nullptr);
        
        if (!connected && GetLastError() != ERROR_PIPE_CONNECTED) {
            DWORD error = GetLastError();
            if (error != ERROR_OPERATION_ABORTED) { // ignore if we're shutting down
                LogToFile("NamedPipeServer: ConnectNamedPipe failed, error: " + std::to_string(error));
            }
            CleanupPipe();
            continue;
        }

        LogToFile("NamedPipeServer: Client connected");
        
        _clientConnected = true;
        
        HandleClientConnection();
        
        // check if client disconnected due to phantom connection detection
        if (!_clientConnected.load()) {
            LogToFile("NamedPipeServer: Phantom connection detected, immediately retrying");
        } else {
            LogToFile("NamedPipeServer: Client disconnected");
        }
        _clientConnected = false;
        CleanupPipe();
    }
    
    LogToFile("NamedPipeServer: Server thread exiting");
}

bool NamedPipeServer::CreatePipeInstance() {
    std::string fullPipeName = "\\\\.\\pipe\\" + _pipeName;
    
    _hPipe = CreateNamedPipeA(
        fullPipeName.c_str(),
        PIPE_ACCESS_DUPLEX,             // bidirectional for heartbeat validation
        PIPE_TYPE_MESSAGE |             // message-type pipe
        PIPE_READMODE_MESSAGE |         // message-read mode
        PIPE_WAIT,                      // blocking mode
        1,                              // max instances (single consumer)
        PIPE_BUFFER_SIZE,               // output buffer size
        PIPE_BUFFER_SIZE,               // input buffer size for heartbeats
        PIPE_TIMEOUT_MS,                // default timeout
        CreatePipeSecurityAttributes()
    );

    if (_hPipe == INVALID_HANDLE_VALUE) {
        DWORD error = GetLastError();
        LogToFile("NamedPipeServer: CreateNamedPipe failed, error: " + std::to_string(error));
        return false;
    }

    LogToFile("NamedPipeServer: Pipe created successfully");
    
    return true;
}

void NamedPipeServer::HandleClientConnection() {
    LogToFile("NamedPipeServer: Client connected, starting handshake...");
    
    // set timeouts for reads and writes
    DWORD timeout = 2000; // 2 seconds
    SetNamedPipeHandleState(_hPipe, nullptr, nullptr, &timeout);
    
    // generate unique connection ID for this session
    uint32_t expectedConnectionId = GetTickCount();
    
    // STEP 1: Send challenge to client
    ConnectionHandshake challenge;
    challenge.magic = 0xDEADBEEF;
    challenge.timestamp = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
    challenge.connectionId = expectedConnectionId;
    
    DWORD bytesWritten;
    if (!WriteFile(_hPipe, &challenge, sizeof(challenge), &bytesWritten, nullptr) || 
        bytesWritten != sizeof(challenge)) {
        LogToFile("NamedPipeServer: Failed to send challenge - phantom connection");
        _clientConnected = false;
        return;
    }
    
    LogToFile("NamedPipeServer: Sent challenge with ID: " + std::to_string(expectedConnectionId));
    
    // STEP 2: Wait for response
    HandshakeResponse response;
    DWORD bytesRead;
    if (!ReadFile(_hPipe, &response, sizeof(response), &bytesRead, nullptr) || 
        bytesRead != sizeof(response)) {
        DWORD error = GetLastError();
        LogToFile("NamedPipeServer: No response received (error: " + std::to_string(error) + ") - phantom connection");
        _clientConnected = false;
        return;
    }
    
    // STEP 3: Validate response
    if (response.magic != 0xBEEFDEAD || response.connectionId != expectedConnectionId) {
        LogToFile("NamedPipeServer: Invalid response (magic: 0x" + std::to_string(response.magic) + 
                  ", ID: " + std::to_string(response.connectionId) + ") - phantom connection");
        _clientConnected = false;
        return;
    }
    
    LogToFile("NamedPipeServer: Valid response received - real client confirmed");
    
    // continue with verified connection
    auto lastActivity = std::chrono::steady_clock::now();
    
    while (_running.load() && _clientConnected.load()) {
        // check for any client data (heartbeats or responses)
        DWORD bytesAvailable = 0;
        if (PeekNamedPipe(_hPipe, nullptr, 0, nullptr, &bytesAvailable, nullptr) && bytesAvailable > 0) {
            BYTE buffer[256];
            DWORD read = 0;
            if (ReadFile(_hPipe, buffer, min(bytesAvailable, 256), &read, nullptr)) {
                lastActivity = std::chrono::steady_clock::now();
            }
        }
        
        // timeout if no activity
        auto now = std::chrono::steady_clock::now();
        if (now - lastActivity > std::chrono::seconds(10)) {
            LogToFile("NamedPipeServer: Client timeout");
            _clientConnected = false;
            break;
        }
        
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
}

GameDataMessage NamedPipeServer::CreateHandshakeMessage() {
    GameDataMessage msg = {};
    msg.messageType = MSG_TYPE_HANDSHAKE;
    msg.sequenceNumber = ++_sequenceNumber;
    msg.timestampMs = static_cast<uint64_t>(
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::system_clock::now().time_since_epoch()).count());
    msg.flags = FLAG_SUCCESS;
    return msg;
}

void NamedPipeServer::CleanupPipe() {
    if (_hPipe != INVALID_HANDLE_VALUE) {
        FlushFileBuffers(_hPipe);  // force any pending data out
        DisconnectNamedPipe(_hPipe);
        CloseHandle(_hPipe);
        _hPipe = INVALID_HANDLE_VALUE;
        
        // give Windows time to fully release the pipe
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
    }
} 