using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ProxChatClient.Services;

// interface for game data reading - allows debug stub implementation
public interface IGameDataReader : IDisposable
{
    event EventHandler<(bool Success, int MapId, string MapName, int X, int Y, string CharacterName, int GameId)>? GameDataRead;
}

public class NamedPipeGameDataReader : IGameDataReader
{
    private const int MESSAGE_SIZE = 56; // sizeof(GameDataMessage) - gameId field replaced reserved1
    private const int PIPE_MESSAGE_SIZE = 72; // sizeof(PipeMessage)
    private const int HEARTBEAT_INTERVAL_MS = 3000;
    private const int CONNECTION_TIMEOUT_MS = 2000; // reverted: keep reasonable timeout for stable connections
    
    private NamedPipeClientStream? _pipeClient;
    private bool _disposed = false;
    private Thread? _readThread;
    private Thread? _heartbeatThread;
    private volatile bool _shouldStop = false;
    private volatile bool _connected = false;
    private readonly DebugLogService _debugLog;
    private readonly string _pipeName;
    private readonly object _pipeLock = new object();
    private DateTime _lastHeartbeatSent = DateTime.MinValue;
    private DateTime _lastDataReceived = DateTime.MinValue;
    
    // add event for new game data
    public event EventHandler<(bool Success, int MapId, string MapName, int X, int Y, string CharacterName, int GameId)>? GameDataRead;
    
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct PipeMessage
    {
        public uint MessageType;  // 0=heartbeat, 1=game_data
        public uint DataSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] Data;
    }

    public NamedPipeGameDataReader(string ipcChannelName, DebugLogService debugLog)
    {
        _pipeName = ipcChannelName ?? throw new ArgumentNullException(nameof(ipcChannelName));
        _debugLog = debugLog;
        _debugLog.LogNamedPipe("CONSTRUCTOR CALLED");

        
        // initialize thread references to null
        _readThread = null;
        _heartbeatThread = null;
        
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
        
        _debugLog.LogNamedPipe("Started named pipe reader and heartbeat threads");
    }

    private void ReadThreadLoop()
    {
        // read thread active
        int messageCount = 0;
        int validMessageCount = 0;
        int eventFireCount = 0;
        int invalidMessageCount = 0;
        int connectionAttempts = 0;
        
        _debugLog.LogNamedPipe("Named pipe reader thread started");
        
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
                        _debugLog.LogNamedPipe($"Connection attempt #{connectionAttempts} failed");
                    }
                    Thread.Sleep(5000); // 5-second intervals for reliable, non-interfering reconnections
                    continue;
                }
                
                if (connectionAttempts > 0)
                {
                    _debugLog.LogNamedPipe($"Connected to server after {connectionAttempts} attempts");
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
                            _debugLog.LogNamedPipe($"Received game data message #{messageCount} (2% sample)");
                        }
                        
                        // log only first message and every 100th for overall count
                        if (messageCount == 1 || messageCount % 100 == 0)
                        {
                            _debugLog.LogNamedPipe($"Received {messageCount} game data messages");
                        }
                        
                        var result = ParseGameDataMessage(pipeMsg.Data);
                        if (result.HasValue)
                        {
                            validMessageCount++;
                            var msg = result.Value;
                            
                            // log only first valid message
                            if (validMessageCount == 1)
                            {
                                _debugLog.LogNamedPipe($"First valid message");
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
                                    _debugLog.LogNamedPipe($"Game data events started: Map={msg.MapId}({mapName}), Pos=({msg.X},{msg.Y}), Char='{characterName}', Flags=0x{msg.Flags:X2}");
                                }
                                
                                // detailed logging for first few events to debug UI issues
                                if (eventFireCount <= 3)
                                {
                                    _debugLog.LogNamedPipe($"Firing GameDataRead event #{eventFireCount}: Success=true, MapId={msg.MapId}, MapName='{mapName}', X={msg.X}, Y={msg.Y}, CharacterName='{characterName}'");
                                }
                                
                                // fire event with game data
                                GameDataRead?.Invoke(this, (true, msg.MapId, mapName, msg.X, msg.Y, characterName, msg.GameId));
                            }
                            else
                            {
                                invalidMessageCount++;
                                
                                // log 2% of invalid messages to avoid spam (every 50th = 2%)
                                if (invalidMessageCount % 50 == 0)
                                {
                                    _debugLog.LogNamedPipe($"Invalid game data #{invalidMessageCount} (2% sample): Success={msg.IsSuccess}, Flags=0x{msg.Flags:X2}, MapId={msg.MapId}, Pos=({msg.X},{msg.Y}), Name='{msg.CharacterName}'");
                                }
                                
                                // fire event indicating failure
                                GameDataRead?.Invoke(this, (false, 0, string.Empty, 0, 0, "Player", 0));
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
                        _debugLog.LogNamedPipe("No data received for too long - connection may be dead");
                        DisconnectPipe();
                    }
                    
                    Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                _debugLog.LogNamedPipe($"Read thread error: {ex.Message}");
                DisconnectPipe();
                Thread.Sleep(5000); // 5-second intervals for reliable, non-interfering reconnections
            }
        }
        
        _debugLog.LogNamedPipe($"Reader stopped. Messages: {messageCount}, Valid: {validMessageCount}, Invalid: {invalidMessageCount}, Events: {eventFireCount}");
    }

    private void HeartbeatThreadLoop()
    {
        // heartbeat thread active
        _debugLog.LogNamedPipe("Heartbeat thread started");
        
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
                            _debugLog.LogNamedPipe("Heartbeat failed - disconnecting");
                            DisconnectPipe();
                        }
                    }
                }
                
                Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                _debugLog.LogNamedPipe($"Heartbeat thread error: {ex.Message}");
                DisconnectPipe();
                Thread.Sleep(1000);
            }
        }
        
        _debugLog.LogNamedPipe("Heartbeat thread stopped");
    }

    private bool EnsureConnection()
    {
        // attempt connection
        if (_connected || _disposed) return _connected;
        
        lock (_pipeLock)
        {
            if (_connected || _disposed) return _connected;
            
            try
            {
                _debugLog.LogNamedPipe($"Attempting to connect to named pipe '{_pipeName}'...");
                
                _pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                
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
                
                _debugLog.LogNamedPipe("Successfully connected to named pipe server");
                return true;
            }
            catch (Exception ex)
            {
                _debugLog.LogNamedPipe($"Connection failed: {ex.Message}");
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
                _debugLog.LogNamedPipe("Disconnecting from named pipe");
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
                _debugLog.LogNamedPipe($"Failed to send heartbeat: {ex.Message}");
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
                _debugLog.LogNamedPipe($"Failed to read from pipe: {ex.Message}");
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
            msg.GameId = BitConverter.ToUInt16(buffer, 18);
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
            _debugLog.LogNamedPipe($"ParseGameDataMessage error: {ex.Message}");
            return null;
        }
    }

    // main method used by viewmodel to read the current game state
    // this is for compatibility with existing code - the event-based approach is preferred
    public (bool Success, int MapId, string MapName, int X, int Y, string CharacterName, int GameId) ReadPositionAndName()
    {
        // for named pipe implementation, we rely on the event callback
        // this method returns the last known state or defaults
        return (false, 0, string.Empty, 0, 0, "Player", 0);
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

// debug stub implementation - never connects to pipe, provides events for debug data
public class DebugGameDataReader : IGameDataReader
{
    public event EventHandler<(bool Success, int MapId, string MapName, int X, int Y, string CharacterName, int GameId)>? GameDataRead;
    
    private readonly DebugLogService _debugLog;
    private bool _disposed = false;

    public DebugGameDataReader(DebugLogService debugLog)
    {
        _debugLog = debugLog;
        _debugLog.LogMain("DebugGameDataReader created - no pipe connection will ever be attempted");
    }

    // method to manually fire game data event from debug UI
    public void FireDebugGameData(int mapId, string mapName, int x, int y, string characterName, int gameId)
    {
        if (!_disposed)
        {
            GameDataRead?.Invoke(this, (true, mapId, mapName, x, y, characterName, gameId));
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _debugLog.LogMain("DebugGameDataReader disposed");
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
} 