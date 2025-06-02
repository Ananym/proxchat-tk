#pragma once
#include <zmq.h>
#include <string>
#include <atomic>
#include <thread>
#include <mutex>
#include "GameDataMessage.h"  // use existing struct definition

class ZeroMQPublisher {
public:
    explicit ZeroMQPublisher(const std::string& endpoint = "ipc://game-data-channel");
    ~ZeroMQPublisher();
    
    bool Start();
    void Stop();
    bool PublishMessage(const GameDataMessage& message);
    bool IsRunning() const { return _running.load(); }
    
private:
    std::string _endpoint;
    void* _context;
    void* _publisher;
    std::atomic<bool> _running;
    std::mutex _publishMutex;
}; 