#include "ZeroMQPublisher.h"
#include <chrono>

// forward declaration from dllmain.cpp
extern void LogToFile(const std::string& message);

ZeroMQPublisher::ZeroMQPublisher(const std::string& endpoint)
    : _endpoint(endpoint)
    , _context(nullptr)
    , _publisher(nullptr)
    , _running(false)
{
    // test zmq linking by getting version
    int major, minor, patch;
    zmq_version(&major, &minor, &patch);
    LogToFile("ZeroMQPublisher: Using ZMQ version " + std::to_string(major) + "." + 
              std::to_string(minor) + "." + std::to_string(patch));
}

ZeroMQPublisher::~ZeroMQPublisher() {
    Stop();
}

bool ZeroMQPublisher::Start() {
    if (_running.load()) {
        LogToFile("ZeroMQPublisher: Already running");
        return true;
    }

    LogToFile("ZeroMQPublisher: Starting publisher on endpoint: " + _endpoint);
    
    // create zmq context
    _context = zmq_ctx_new();
    if (!_context) {
        LogToFile("ZeroMQPublisher: Failed to create ZMQ context");
        return false;
    }
    
    // create publisher socket
    _publisher = zmq_socket(_context, ZMQ_PUB);
    if (!_publisher) {
        LogToFile("ZeroMQPublisher: Failed to create ZMQ publisher socket");
        zmq_ctx_destroy(_context);
        _context = nullptr;
        return false;
    }
    
    // set socket options for faster reconnection
    int linger = 0; // immediate close, don't wait for pending messages
    zmq_setsockopt(_publisher, ZMQ_LINGER, &linger, sizeof(linger));
    
    int immediate = 1; // don't queue messages when no peers connected
    zmq_setsockopt(_publisher, ZMQ_IMMEDIATE, &immediate, sizeof(immediate));
    
    // set high water mark to prevent memory buildup if no subscribers
    int hwm = 50; // lower for ipc since it's faster
    zmq_setsockopt(_publisher, ZMQ_SNDHWM, &hwm, sizeof(hwm));
    
    // bind to endpoint
    int rc = zmq_bind(_publisher, _endpoint.c_str());
    if (rc != 0) {
        LogToFile("ZeroMQPublisher: Failed to bind to " + _endpoint + ", error: " + std::to_string(zmq_errno()));
        zmq_close(_publisher);
        zmq_ctx_destroy(_context);
        _publisher = nullptr;
        _context = nullptr;
        return false;
    }
    
    _running = true;
    LogToFile("ZeroMQPublisher: Started successfully");
    return true;
}

void ZeroMQPublisher::Stop() {
    if (!_running.load()) return;
    
    LogToFile("ZeroMQPublisher: Stopping publisher");
    _running = false;
    
    if (_publisher) {
        zmq_close(_publisher);
        _publisher = nullptr;
    }
    
    if (_context) {
        zmq_ctx_destroy(_context);
        _context = nullptr;
    }
    
    LogToFile("ZeroMQPublisher: Stopped");
}

bool ZeroMQPublisher::PublishMessage(const GameDataMessage& message) {
    if (!_running.load() || !_publisher) {
        static int notRunningCount = 0;
        if (++notRunningCount <= 3) { // only log first few times
            LogToFile("ZeroMQPublisher: PublishMessage called but not running or no publisher");
        }
        return false;
    }
    
    std::lock_guard<std::mutex> lock(_publishMutex);
    
    // send with DONTWAIT to avoid blocking - if no subscribers or network issues, just drop the message
    int rc = zmq_send(_publisher, &message, sizeof(GameDataMessage), ZMQ_DONTWAIT);
    
    if (rc == -1) {
        int err = zmq_errno();
        static int lastLoggedError = -1;
        static int errorCount = 0;
        
        if (err != EAGAIN) { // EAGAIN is normal when no subscribers, don't log it
            if (err != lastLoggedError || errorCount++ % 20 == 0) { // log new errors or every 20th repeat
                LogToFile("ZeroMQPublisher: Send failed, error: " + std::to_string(err));
                lastLoggedError = err;
            }
        } else {
            static int eagainCount = 0;
            if (++eagainCount <= 5) { // log first few EAGAIN errors to see if subscriber connects
                LogToFile("ZeroMQPublisher: No subscribers connected (EAGAIN)");
            }
        }
        return false;
    }
    
    // log successful sends occasionally
    static int successCount = 0;
    if (++successCount <= 5 || successCount % 100 == 0) {
        LogToFile("ZeroMQPublisher: Successfully sent message #" + std::to_string(successCount));
    }
    
    return true;
} 