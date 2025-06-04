using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NetMQ;
using NetMQ.Sockets;

namespace ProxChatClient.Services;

public class ZeroMQGameDataReader : IDisposable
{
    private readonly string _zmqEndpoint;
    private const int MESSAGE_SIZE = 64; // sizeof(GameDataMessage)
    
    private SubscriberSocket? _subscriber;
    private bool _disposed = false;
    private uint _lastSequenceNumber = 0;
    private Thread? _readThread;
    private volatile bool _shouldStop = false;
    private readonly DebugLogService? _debugLog;
    
    // add event for new game data
    public event EventHandler<(bool Success, int MapId, string MapName, int X, int Y, string CharacterName)>? GameDataRead;

    public ZeroMQGameDataReader(string ipcChannelName = "game-data-channel", DebugLogService? debugLog = null)
    {
        _zmqEndpoint = "ipc://proxchat";
        _debugLog = debugLog;
        _debugLog?.LogNamedPipe($"Initializing ZeroMQGameDataReader for endpoint '{_zmqEndpoint}'");
        
        // start dedicated read thread
        _readThread = new Thread(ReadThreadLoop)
        {
            IsBackground = true,
            Name = "ZeroMQReader"
        };
        _readThread.Start();
        _debugLog?.LogNamedPipe("Started ZeroMQ reader thread");
    }

    private void ReadThreadLoop()
    {
        int frameReceiveCount = 0;
        int validMessageCount = 0;
        int eventFireCount = 0;
        int timeoutCount = 0;
        int consecutiveTimeouts = 0;
        bool wasConnected = false;
        
        _debugLog?.LogNamedPipe("ZeroMQ reader thread started");
        
        while (!_shouldStop && !_disposed)
        {
            try
            {
                EnsureConnection();
                
                if (_subscriber != null)
                {
                    // poll with timeout appropriate for real-time game data
                    if (_subscriber.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(50), out var messageBytes))
                    {
                        // got message - reset timeout tracking
                        consecutiveTimeouts = 0;
                        timeoutCount = 0;
                        
                        if (!wasConnected) 
                        {
                            _debugLog?.LogNamedPipe("Connected to publisher and receiving data");
                            wasConnected = true;
                        }
                        
                        frameReceiveCount++;
                        
                        // log only first message and every 100th
                        if (frameReceiveCount == 1 || frameReceiveCount % 100 == 0)
                        {
                            _debugLog?.LogNamedPipe($"Received {frameReceiveCount} messages");
                        }
                        
                        if (messageBytes != null && messageBytes.Length == MESSAGE_SIZE)
                        {
                            var result = ParseMessage(messageBytes);
                            if (result.HasValue)
                            {
                                validMessageCount++;
                                var msg = result.Value;
                                
                                // log only first valid message
                                if (validMessageCount == 1)
                                {
                                    _debugLog?.LogNamedPipe($"First valid message: SeqNum={msg.SequenceNumber}, Type={msg.MessageType}");
                                }
                                
                                // check for duplicate messages
                                if (msg.SequenceNumber <= _lastSequenceNumber && _lastSequenceNumber != 0)
                                {
                                    continue; // skip duplicate or out-of-order message
                                }
                                _lastSequenceNumber = msg.SequenceNumber;
                                
                                // validate timestamp (must be within 10 seconds)
                                var age = DateTime.UtcNow - msg.Timestamp;
                                if (age.TotalSeconds > 10.0)
                                {
                                    continue; // silently skip old messages
                                }
                                
                                if (msg.MessageType == MessageType.GameData && msg.IsSuccess && msg.IsPositionValid)
                                {
                                    eventFireCount++;
                                    if (eventFireCount == 1)
                                    {
                                        _debugLog?.LogNamedPipe($"Game data events started: Map={msg.MapId}, Pos=({msg.X},{msg.Y})");
                                    }
                                    
                                    // fire event with game data
                                    GameDataRead?.Invoke(this, (true, msg.MapId, msg.MapName, msg.X, msg.Y, msg.CharacterName));
                                }
                                else
                                {
                                    // fire event indicating failure (only log first few)
                                    if (eventFireCount < 3)
                                    {
                                        _debugLog?.LogNamedPipe($"Invalid game data: Type={msg.MessageType}, Success={msg.IsSuccess}");
                                    }
                                    GameDataRead?.Invoke(this, (false, 0, string.Empty, 0, 0, "Player"));
                                }
                            }
                        }
                    }
                    else
                    {
                        // timeout - track consecutive timeouts to detect disconnect
                        timeoutCount++;
                        consecutiveTimeouts++;
                        
                        // much faster disconnect detection for local ipc (5 timeouts = 250ms)
                        if (consecutiveTimeouts == 5 && wasConnected)
                        {
                            _debugLog?.LogNamedPipe("Publisher disconnected, attempting reconnection...");
                            wasConnected = false;
                            GameDataRead?.Invoke(this, (false, 0, string.Empty, 0, 0, "Player"));
                            CleanupConnection();
                        }
                        // aggressive reconnection when disconnected
                        else if (!wasConnected && consecutiveTimeouts % 10 == 0)
                        {
                            _debugLog?.LogNamedPipe($"Still disconnected after {consecutiveTimeouts} attempts");
                            CleanupConnection();
                        }
                        
                        // log timeouts much less frequently
                        if (timeoutCount == 1 || timeoutCount % 200 == 0)
                        {
                            _debugLog?.LogNamedPipe($"No data received (timeout #{timeoutCount})");
                        }
                    }
                }
                else
                {
                    // no connection, wait short period for reconnect
                    Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                _debugLog?.LogNamedPipe($"Read thread error: {ex.Message}");
                CleanupConnection();
                Thread.Sleep(100);
            }
        }
        
        _debugLog?.LogNamedPipe($"Reader stopped. Received: {frameReceiveCount}, Valid: {validMessageCount}, Events: {eventFireCount}");
    }

    private void EnsureConnection()
    {
        if (_subscriber == null && !_disposed)
        {
            try
            {
                _debugLog?.LogNamedPipe($"Connecting to ZeroMQ endpoint '{_zmqEndpoint}'...");
                _subscriber = new SubscriberSocket();
                
                // optimal socket options for local ipc real-time game data
                _subscriber.Options.ReceiveHighWatermark = 5; // very small queue - we want latest data only
                _subscriber.Options.Linger = TimeSpan.Zero; // immediate close, don't wait
                
                // aggressive reconnection for local ipc
                _subscriber.Options.ReconnectInterval = TimeSpan.FromMilliseconds(10); // start fast
                _subscriber.Options.ReconnectIntervalMax = TimeSpan.FromMilliseconds(100); // low max for local ipc
                
                // connection attempts - more aggressive for local
                _subscriber.Options.Backlog = 0; // no connection backlog queue needed for single publisher
                
                _subscriber.Connect(_zmqEndpoint);
                
                // subscribe to all messages (empty subscription = receive everything)
                _subscriber.Subscribe("");
                
                // brief delay for subscription establishment
                Thread.Sleep(10);
                
                _debugLog?.LogNamedPipe("ZeroMQ socket connected and subscribed");
            }
            catch (Exception ex)
            {
                _debugLog?.LogNamedPipe($"Connection failed: {ex.Message}");
                CleanupConnection();
            }
        }
    }

    private void CleanupConnection()
    {
        if (_subscriber != null)
        {
            try 
            { 
                _subscriber.Dispose();
                _debugLog?.LogNamedPipe("Socket disposed for reconnection");
            } 
            catch { }
            finally
            {
                _subscriber = null;
            }
        }
    }

    private GameDataMessage? ParseMessage(byte[] buffer)
    {
        try
        {
            var msg = new GameDataMessage();
            msg.MessageType = BitConverter.ToUInt32(buffer, 0);
            msg.SequenceNumber = BitConverter.ToUInt32(buffer, 4);
            msg.TimestampMs = BitConverter.ToUInt64(buffer, 8);
            msg.X = BitConverter.ToInt32(buffer, 16);
            msg.Y = BitConverter.ToInt32(buffer, 20);
            msg.MapId = BitConverter.ToUInt16(buffer, 24);
            msg.Reserved1 = BitConverter.ToUInt16(buffer, 26);
            msg.MapNameBytes = new byte[16];
            msg.CharacterNameBytes = new byte[12];
            Array.Copy(buffer, 28, msg.MapNameBytes, 0, 16);
            Array.Copy(buffer, 44, msg.CharacterNameBytes, 0, 12);
            msg.Flags = BitConverter.ToUInt32(buffer, 56);
            msg.Reserved2 = BitConverter.ToUInt32(buffer, 60);
            
            return msg;
        }
        catch (Exception ex)
        {
            _debugLog?.LogNamedPipe($"ParseMessage error: {ex.Message}");
            return null;
        }
    }

    // main method used by viewmodel to read the current game state
    // this is for compatibility with existing code - the event-based approach is preferred
    public (bool Success, int MapId, string MapName, int X, int Y, string CharacterName) ReadPositionAndName()
    {
        // for zeromq implementation, we rely on the event callback
        // this method returns the last known state or defaults
        return (false, 0, string.Empty, 0, 0, "Player");
    }

    public void Close()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _shouldStop = true;
                _readThread?.Join(TimeSpan.FromSeconds(5));
                CleanupConnection();
                NetMQConfig.Cleanup(); // cleanup netmq resources
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Close();
    }

    ~ZeroMQGameDataReader()
    {
        Dispose(disposing: false);
    }
} 