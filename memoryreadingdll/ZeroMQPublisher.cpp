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
    
    // optimal socket options for local ipc real-time game data
    int linger = 0; // immediate close, don't wait for pending messages
    zmq_setsockopt(_publisher, ZMQ_LINGER, &linger, sizeof(linger));
    
    // for real-time game data, drop messages if no subscribers rather than queue
    int immediate = 1; // don't queue messages when no peers connected
    zmq_setsockopt(_publisher, ZMQ_IMMEDIATE, &immediate, sizeof(immediate));
    
    // low hwm for real-time data - we want recent data, not old queued data
    int hwm = 10; // very small queue for real-time game data
    zmq_setsockopt(_publisher, ZMQ_SNDHWM, &hwm, sizeof(hwm));
    
    // send timeout to prevent blocking on send
    int send_timeout = 0; // non-blocking sends for real-time data
    zmq_setsockopt(_publisher, ZMQ_SNDTIMEO, &send_timeout, sizeof(send_timeout));
    
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
    
    // static counters for monitoring
    static int lastLoggedError = -1;
    static int errorCount = 0;
    static int consecutiveEagain = 0;
    static int successCount = 0;
    
    // send with DONTWAIT to avoid blocking - if no subscribers or network issues, just drop the message
    int rc = zmq_send(_publisher, &message, sizeof(GameDataMessage), ZMQ_DONTWAIT);
    
    if (rc == -1) {
        int err = zmq_errno();
        
        if (err == EAGAIN) {
            consecutiveEagain++;
            // only log if this is a new issue
            if (consecutiveEagain == 1) {
                LogToFile("ZeroMQPublisher: No subscribers connected (will not log further EAGAIN)");
            } else if (consecutiveEagain > 100) { // ~10 seconds of failed sends
                LogToFile("ZeroMQPublisher: Extended period without subscribers");
                consecutiveEagain = 0; // reset counter
            }
        } else {
            consecutiveEagain = 0; // reset on non-EAGAIN errors
            if (err != lastLoggedError) { // only log new error types
                LogToFile("ZeroMQPublisher: Send failed, error: " + std::to_string(err));
                lastLoggedError = err;
                errorCount = 0;
            }
        }
        return false;
    }
    
    // successful send - reset counters
    consecutiveEagain = 0;
    successCount++;
    
    // only log first successful message
    if (successCount == 1) {
        LogToFile("ZeroMQPublisher: Started sending messages successfully");
    }
    
    return true;
} 