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
        _zmqEndpoint = "ipc://proxchat-gamedata";
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
        
        while (!_shouldStop && !_disposed)
        {
            try
            {
                EnsureConnection();
                
                if (_subscriber != null)
                {
                    // try to receive with shorter timeout for more responsive reconnection
                    if (_subscriber.TryReceiveFrameBytes(TimeSpan.FromMilliseconds(100), out byte[]? frameBytes))
                    {
                        frameReceiveCount++;
                        consecutiveTimeouts = 0; // reset timeout counter on successful receive
                        
                        if (!wasConnected)
                        {
                            _debugLog?.LogNamedPipe("Publisher reconnected - receiving frames again");
                            wasConnected = true;
                        }
                        
                        // log first few received frames
                        if (frameReceiveCount <= 5 || frameReceiveCount % 100 == 0)
                        {
                            _debugLog?.LogNamedPipe($"Received frame #{frameReceiveCount}, size: {frameBytes?.Length ?? 0} bytes");
                        }
                        
                        if (frameBytes != null && frameBytes.Length == MESSAGE_SIZE)
                        {
                            var result = ParseMessage(frameBytes);
                            if (result.HasValue)
                            {
                                validMessageCount++;
                                var msg = result.Value;
                                
                                // log first few valid messages  
                                if (validMessageCount <= 5)
                                {
                                    _debugLog?.LogNamedPipe($"Valid message #{validMessageCount}: SeqNum={msg.SequenceNumber}, Type={msg.MessageType}, Success={msg.IsSuccess}");
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
                                    if (eventFireCount < 3) // log first few timestamp rejections
                                    {
                                        _debugLog?.LogNamedPipe($"Message rejected: timestamp too old ({age.TotalSeconds:F1}s)");
                                    }
                                    continue;
                                }
                                
                                if (msg.MessageType == MessageType.GameData && msg.IsSuccess && msg.IsPositionValid)
                                {
                                    eventFireCount++;
                                    if (eventFireCount <= 5)
                                    {
                                        _debugLog?.LogNamedPipe($"Firing GameDataRead event #{eventFireCount}: Map={msg.MapId}, Pos=({msg.X},{msg.Y}), Name='{msg.CharacterName}'");
                                    }
                                    
                                    // fire event with game data
                                    GameDataRead?.Invoke(this, (true, msg.MapId, msg.MapName, msg.X, msg.Y, msg.CharacterName));
                                }
                                else
                                {
                                    if (eventFireCount < 3) // log first few failures
                                    {
                                        _debugLog?.LogNamedPipe($"Message validation failed: Type={msg.MessageType}, Success={msg.IsSuccess}, PosValid={msg.IsPositionValid}");
                                    }
                                    
                                    // fire event indicating failure
                                    GameDataRead?.Invoke(this, (false, 0, string.Empty, 0, 0, "Player"));
                                }
                            }
                            else
                            {
                                if (validMessageCount < 3) // log first few parse failures
                                {
                                    _debugLog?.LogNamedPipe($"Failed to parse message frame (size: {frameBytes.Length})");
                                }
                            }
                        }
                        else
                        {
                            if (frameReceiveCount <= 5) // log first few size mismatches
                            {
                                _debugLog?.LogNamedPipe($"Frame size mismatch: got {frameBytes?.Length ?? 0}, expected {MESSAGE_SIZE}");
                            }
                        }
                    }
                    else
                    {
                        // timeout - track consecutive timeouts to detect disconnect
                        timeoutCount++;
                        consecutiveTimeouts++;
                        
                        // detect disconnection faster for ipc (10 timeouts = 1 second)
                        if (consecutiveTimeouts == 10 && wasConnected)
                        {
                            _debugLog?.LogNamedPipe("Publisher appears to have disconnected (10+ consecutive timeouts)");
                            _debugLog?.LogNamedPipe("Forcing socket recreation for faster reconnection...");
                            wasConnected = false;
                            // fire failure event to update UI
                            GameDataRead?.Invoke(this, (false, 0, string.Empty, 0, 0, "Player"));
                            // force socket recreation rather than relying on automatic reconnection
                            CleanupConnection();
                        }
                        // rapid reconnection mode - if disconnected, force reconnection every 2 seconds for ipc
                        else if (!wasConnected && consecutiveTimeouts % 20 == 0)
                        {
                            _debugLog?.LogNamedPipe($"Still disconnected after {consecutiveTimeouts} timeouts - forcing socket recreation...");
                            CleanupConnection();
                        }
                        
                        if (timeoutCount <= 10 || timeoutCount % 100 == 0)
                        {
                            _debugLog?.LogNamedPipe($"No frame received (timeout #{timeoutCount}, consecutive: {consecutiveTimeouts})");
                        }
                    }
                }
                else
                {
                    // no connection, wait shorter period for faster reconnect attempts with ipc
                    Thread.Sleep(25); // faster retry for local ipc
                }
            }
            catch (Exception ex)
            {
                _debugLog?.LogNamedPipe($"Read thread error: {ex.Message}");
                CleanupConnection();
                // very short sleep on error for fastest recovery
                Thread.Sleep(100);
            }
        }
        
        _debugLog?.LogNamedPipe($"Read thread stopping. Frames received: {frameReceiveCount}, Valid messages: {validMessageCount}, Events fired: {eventFireCount}, Timeouts: {timeoutCount}");
    }

    private void EnsureConnection()
    {
        if (_subscriber == null && !_disposed)
        {
            try
            {
                _debugLog?.LogNamedPipe($"Connecting to ZeroMQ endpoint '{_zmqEndpoint}'...");
                _subscriber = new SubscriberSocket();
                
                // aggressive socket options optimized for local ipc
                _subscriber.Options.ReceiveHighWatermark = 50; // lower for ipc
                _subscriber.Options.Linger = TimeSpan.Zero; // immediate close, don't wait
                _subscriber.Options.ReconnectInterval = TimeSpan.FromMilliseconds(10); // very fast reconnect for ipc
                _subscriber.Options.ReconnectIntervalMax = TimeSpan.FromMilliseconds(100); // max backoff very low for local ipc
                
                _subscriber.Connect(_zmqEndpoint);
                _debugLog?.LogNamedPipe($"Socket connected to '{_zmqEndpoint}'");
                
                // subscribe to all messages (empty subscription = receive everything)
                _subscriber.Subscribe("");
                _debugLog?.LogNamedPipe("Subscribed to all messages");
                
                // minimal delay for ipc subscription establishment
                Thread.Sleep(10); // much shorter for local ipc
                
                _debugLog?.LogNamedPipe("Connected to ZeroMQ successfully");
            }
            catch (Exception ex)
            {
                _debugLog?.LogNamedPipe($"Connection failed: {ex.Message}");
                _debugLog?.LogNamedPipe($"Exception type: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    _debugLog?.LogNamedPipe($"Inner exception: {ex.InnerException.Message}");
                }
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
            } 
            catch { }
            _subscriber = null;
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