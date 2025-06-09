using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ProxChatClient.Services;

public class NamedPipeGameDataReader : IDisposable
{
    private const string PIPE_NAME = "gamedata";
    private const int MESSAGE_SIZE = 56; // sizeof(GameDataMessage) - updated for simplified structure
    private const int PIPE_MESSAGE_SIZE = 72; // sizeof(PipeMessage)
    private const int HEARTBEAT_INTERVAL_MS = 3000;
    private const int CONNECTION_TIMEOUT_MS = 2000; // reverted: keep reasonable timeout for stable connections
    
    private NamedPipeClientStream? _pipeClient;
    private bool _disposed = false;
    private Thread? _readThread;
    private Thread? _heartbeatThread;
    private volatile bool _shouldStop = false;
    private volatile bool _connected = false;
    private readonly DebugLogService? _debugLog;
    private readonly object _pipeLock = new object();
    private DateTime _lastHeartbeatSent = DateTime.MinValue;
    private DateTime _lastDataReceived = DateTime.MinValue;
    
    // add event for new game data
    public event EventHandler<(bool Success, int MapId, string MapName, int X, int Y, string CharacterName)>? GameDataRead;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct PipeMessage
    {
        public uint MessageType;  // 0=heartbeat, 1=game_data
        public uint DataSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] Data;
    }

    public NamedPipeGameDataReader(string ipcChannelName = "game-data-channel", DebugLogService? debugLog = null)
    {
        _debugLog = debugLog;
        _debugLog?.LogNamedPipe($"Initializing NamedPipeGameDataReader for pipe '{PIPE_NAME}'");
        
        // start dedicated threads
        _readThread = new Thread(ReadThreadLoop)
        {
            IsBackground = true,
            Name = "NamedPipeReader"
        };
        _readThread.Start();
        
        _heartbeatThread = new Thread(HeartbeatThreadLoop)
        {
            IsBackground = true,
            Name = "NamedPipeHeartbeat"
        };
        _heartbeatThread.Start();
        
        _debugLog?.LogNamedPipe("Started named pipe reader and heartbeat threads");
    }

    private void ReadThreadLoop()
    {
        int messageCount = 0;
        int validMessageCount = 0;
        int eventFireCount = 0;
        int connectionAttempts = 0;
        
        _debugLog?.LogNamedPipe("Named pipe reader thread started");
        
        while (!_shouldStop && !_disposed)
        {
            try
            {
                // ensure we have a connection
                if (!_connected && !EnsureConnection())
                {
                    connectionAttempts++;
                    if (connectionAttempts % 5 == 1) // log every 5 attempts (every ~25 seconds)
                    {
                        _debugLog?.LogNamedPipe($"Connection attempt #{connectionAttempts} failed");
                    }
                    Thread.Sleep(5000); // 5-second intervals for reliable, non-interfering reconnections
                    continue;
                }
                
                if (connectionAttempts > 0)
                {
                    _debugLog?.LogNamedPipe($"Connected to server after {connectionAttempts} attempts");
                    connectionAttempts = 0;
                }

                // read message from pipe
                var messageBytes = ReadPipeMessage();
                if (messageBytes != null)
                {
                    var pipeMsg = BytesToPipeMessage(messageBytes);
                    
                    if (pipeMsg.MessageType == 0)
                    {
                        // heartbeat received - connection is healthy
                        _lastDataReceived = DateTime.UtcNow;
                    }
                    else if (pipeMsg.MessageType == 1 && pipeMsg.DataSize == MESSAGE_SIZE)
                    {
                        // game data message
                        messageCount++;
                        _lastDataReceived = DateTime.UtcNow;
                        
                        // log 2% of messages to track reception without excessive verbosity
                        if (messageCount % 50 == 0) // log every 50th message = 2%
                        {
                            _debugLog?.LogNamedPipe($"Received game data message #{messageCount} (2% sample)");
                        }
                        
                        // log only first message and every 100th for overall count
                        if (messageCount == 1 || messageCount % 100 == 0)
                        {
                            _debugLog?.LogNamedPipe($"Received {messageCount} game data messages");
                        }
                        
                        var result = ParseGameDataMessage(pipeMsg.Data);
                        if (result.HasValue)
                        {
                            validMessageCount++;
                            var msg = result.Value;
                            
                            // log only first valid message
                            if (validMessageCount == 1)
                            {
                                _debugLog?.LogNamedPipe($"First valid message");
                            }
                            
                            // validate timestamp (must be within 10 seconds)
                            var age = DateTime.UtcNow - msg.Timestamp;
                            if (age.TotalSeconds > 10.0)
                            {
                                continue; // silently skip old messages
                            }
                            
                            if (msg.IsSuccess)
                            {
                                eventFireCount++;
                                
                                string characterName = msg.CharacterName;
                                string mapName = msg.MapName;
                                
                                if (eventFireCount == 1)
                                {
                                    _debugLog?.LogNamedPipe($"Game data events started: Map={msg.MapId}({mapName}), Pos=({msg.X},{msg.Y}), Char='{characterName}', Flags=0x{msg.Flags:X2}");
                                }
                                
                                // detailed logging for first few events to debug UI issues
                                if (eventFireCount <= 3)
                                {
                                    _debugLog?.LogNamedPipe($"Firing GameDataRead event #{eventFireCount}: Success=true, MapId={msg.MapId}, MapName='{mapName}', X={msg.X}, Y={msg.Y}, CharacterName='{characterName}'");
                                }
                                
                                // fire event with game data
                                GameDataRead?.Invoke(this, (true, msg.MapId, mapName, msg.X, msg.Y, characterName));
                            }
                            else
                            {
                                // fire event indicating failure (only log first few)
                                if (eventFireCount < 3)
                                {
                                    _debugLog?.LogNamedPipe($"Invalid game data: Success={msg.IsSuccess}, Flags=0x{msg.Flags:X2}, MapId={msg.MapId}, Pos=({msg.X},{msg.Y}), Name='{msg.CharacterName}'");
                                }
                                GameDataRead?.Invoke(this, (false, 0, string.Empty, 0, 0, "Player"));
                            }
                        }
                    }
                }
                else
                {
                    // no data available, check if connection is still alive
                    var timeSinceLastData = DateTime.UtcNow - _lastDataReceived;
                    if (timeSinceLastData.TotalMilliseconds > HEARTBEAT_INTERVAL_MS * 3)
                    {
                        _debugLog?.LogNamedPipe("No data received for too long - connection may be dead");
                        DisconnectPipe();
                    }
                    
                    Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                _debugLog?.LogNamedPipe($"Read thread error: {ex.Message}");
                DisconnectPipe();
                Thread.Sleep(5000); // 5-second intervals for reliable, non-interfering reconnections
            }
        }
        
        _debugLog?.LogNamedPipe($"Reader stopped. Messages: {messageCount}, Valid: {validMessageCount}, Events: {eventFireCount}");
    }

    private void HeartbeatThreadLoop()
    {
        _debugLog?.LogNamedPipe("Heartbeat thread started");
        
        while (!_shouldStop && !_disposed)
        {
            try
            {
                if (_connected)
                {
                    var timeSinceLastHeartbeat = DateTime.UtcNow - _lastHeartbeatSent;
                    if (timeSinceLastHeartbeat.TotalMilliseconds >= HEARTBEAT_INTERVAL_MS)
                    {
                        if (SendHeartbeat())
                        {
                            _lastHeartbeatSent = DateTime.UtcNow;
                        }
                        else
                        {
                            _debugLog?.LogNamedPipe("Heartbeat failed - disconnecting");
                            DisconnectPipe();
                        }
                    }
                }
                
                Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                _debugLog?.LogNamedPipe($"Heartbeat thread error: {ex.Message}");
                DisconnectPipe();
                Thread.Sleep(1000);
            }
        }
        
        _debugLog?.LogNamedPipe("Heartbeat thread stopped");
    }

    private bool EnsureConnection()
    {
        if (_connected || _disposed) return _connected;
        
        lock (_pipeLock)
        {
            if (_connected || _disposed) return _connected;
            
            try
            {
                _debugLog?.LogNamedPipe($"Attempting to connect to named pipe '{PIPE_NAME}'...");
                
                _pipeClient = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.InOut, PipeOptions.Asynchronous);
                
                // try to connect with timeout
                var connectTask = Task.Run(() => _pipeClient.Connect(CONNECTION_TIMEOUT_MS));
                if (!connectTask.Wait(CONNECTION_TIMEOUT_MS + 500)) // reverted: keep reasonable margin for stable connections
                {
                    _pipeClient?.Dispose();
                    _pipeClient = null;
                    return false;
                }
                
                _connected = true;
                _lastDataReceived = DateTime.UtcNow;
                _lastHeartbeatSent = DateTime.MinValue; // force immediate heartbeat
                
                _debugLog?.LogNamedPipe("Successfully connected to named pipe server");
                return true;
            }
            catch (Exception ex)
            {
                _debugLog?.LogNamedPipe($"Connection failed: {ex.Message}");
                _pipeClient?.Dispose();
                _pipeClient = null;
                return false;
            }
        }
    }

    private void DisconnectPipe()
    {
        lock (_pipeLock)
        {
            if (_connected)
            {
                _debugLog?.LogNamedPipe("Disconnecting from named pipe");
                _connected = false;
            }
            
            try
            {
                _pipeClient?.Close();
                _pipeClient?.Dispose();
            }
            catch { }
            finally
            {
                _pipeClient = null;
            }
        }
    }

    private bool SendHeartbeat()
    {
        lock (_pipeLock)
        {
            if (!_connected || _pipeClient == null) return false;
            
            try
            {
                var heartbeatMsg = new PipeMessage
                {
                    MessageType = 0, // heartbeat
                    DataSize = 0,
                    Data = new byte[64]
                };
                
                var messageBytes = PipeMessageToBytes(heartbeatMsg);
                _pipeClient.Write(messageBytes, 0, messageBytes.Length);
                _pipeClient.Flush();
                return true;
            }
            catch (Exception ex)
            {
                _debugLog?.LogNamedPipe($"Failed to send heartbeat: {ex.Message}");
                return false;
            }
        }
    }

    private byte[]? ReadPipeMessage()
    {
        lock (_pipeLock)
        {
            if (!_connected || _pipeClient == null) return null;
            
            try
            {
                // check if data is available without blocking
                if (!_pipeClient.IsConnected)
                {
                    return null;
                }
                
                var buffer = new byte[PIPE_MESSAGE_SIZE];
                
                // use synchronous read with the pipe's built-in timeout
                int totalBytesRead = 0;
                while (totalBytesRead < PIPE_MESSAGE_SIZE && _pipeClient.IsConnected)
                {
                    int bytesRead = _pipeClient.Read(buffer, totalBytesRead, PIPE_MESSAGE_SIZE - totalBytesRead);
                    if (bytesRead == 0)
                    {
                        // pipe closed
                        return null;
                    }
                    totalBytesRead += bytesRead;
                }
                
                return totalBytesRead == PIPE_MESSAGE_SIZE ? buffer : null;
            }
            catch (TimeoutException)
            {
                // normal timeout, not an error
                return null;
            }
            catch (Exception ex)
            {
                _debugLog?.LogNamedPipe($"Failed to read from pipe: {ex.Message}");
                return null;
            }
        }
    }

    private PipeMessage BytesToPipeMessage(byte[] bytes)
    {
        var msg = new PipeMessage();
        msg.MessageType = BitConverter.ToUInt32(bytes, 0);
        msg.DataSize = BitConverter.ToUInt32(bytes, 4);
        msg.Data = new byte[64];
        Array.Copy(bytes, 8, msg.Data, 0, 64);
        return msg;
    }

    private byte[] PipeMessageToBytes(PipeMessage msg)
    {
        var buffer = new byte[PIPE_MESSAGE_SIZE];
        BitConverter.GetBytes(msg.MessageType).CopyTo(buffer, 0);
        BitConverter.GetBytes(msg.DataSize).CopyTo(buffer, 4);
        msg.Data.CopyTo(buffer, 8);
        return buffer;
    }

    private GameDataMessage? ParseGameDataMessage(byte[] buffer)
    {
        try
        {
            var msg = new GameDataMessage();
            msg.TimestampMs = BitConverter.ToUInt64(buffer, 0);
            msg.X = BitConverter.ToInt32(buffer, 8);
            msg.Y = BitConverter.ToInt32(buffer, 12);
            msg.MapId = BitConverter.ToUInt16(buffer, 16);
            msg.Reserved1 = BitConverter.ToUInt16(buffer, 18);
            msg.MapNameBytes = new byte[16];
            msg.CharacterNameBytes = new byte[12];
            Array.Copy(buffer, 20, msg.MapNameBytes, 0, 16);
            Array.Copy(buffer, 36, msg.CharacterNameBytes, 0, 12);
            msg.Flags = BitConverter.ToUInt32(buffer, 48);
            msg.Reserved2 = BitConverter.ToUInt32(buffer, 52);
            
            return msg;
        }
        catch (Exception ex)
        {
            _debugLog?.LogNamedPipe($"ParseGameDataMessage error: {ex.Message}");
            return null;
        }
    }

    // main method used by viewmodel to read the current game state
    // this is for compatibility with existing code - the event-based approach is preferred
    public (bool Success, int MapId, string MapName, int X, int Y, string CharacterName) ReadPositionAndName()
    {
        // for named pipe implementation, we rely on the event callback
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
                _heartbeatThread?.Join(TimeSpan.FromSeconds(5));
                DisconnectPipe();
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Close();
    }

    ~NamedPipeGameDataReader()
    {
        Dispose(disposing: false);
    }
} 