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
        
        LogToFile("NamedPipeServer: Client disconnected");
        _clientConnected = false;
        CleanupPipe();
    }
    
    LogToFile("NamedPipeServer: Server thread exiting");
}

bool NamedPipeServer::CreatePipeInstance() {
    std::string fullPipeName = "\\\\.\\pipe\\" + _pipeName;
    
    _hPipe = CreateNamedPipeA(
        fullPipeName.c_str(),
        PIPE_ACCESS_OUTBOUND,           // server writes to client
        PIPE_TYPE_MESSAGE |             // message-type pipe
        PIPE_READMODE_MESSAGE |         // message-read mode
        PIPE_WAIT,                      // blocking mode
        1,                              // max instances (single consumer)
        PIPE_BUFFER_SIZE,               // output buffer size
        0,                              // input buffer size (not used)
        PIPE_TIMEOUT_MS,                // default timeout
        CreatePipeSecurityAttributes()
    );

    if (_hPipe == INVALID_HANDLE_VALUE) {
        DWORD error = GetLastError();
        LogToFile("NamedPipeServer: CreateNamedPipe failed, error: " + std::to_string(error));
        return false;
    }

    return true;
}

void NamedPipeServer::HandleClientConnection() {
    // simply wait for client to disconnect or shutdown
    // no need to actively check connection status with writes
    while (_running.load() && _clientConnected.load()) {
        std::this_thread::sleep_for(std::chrono::milliseconds(500));
    }
}

void NamedPipeServer::CleanupPipe() {
    if (_hPipe != INVALID_HANDLE_VALUE) {
        DisconnectNamedPipe(_hPipe);
        CloseHandle(_hPipe);
        _hPipe = INVALID_HANDLE_VALUE;
    }
} 