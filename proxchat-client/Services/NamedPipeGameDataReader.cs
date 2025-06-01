using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ProxChatClient.Services;

// active challenge-response handshake structures for phantom connection detection
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct ConnectionHandshake
{
    public uint Magic;           // 0xDEADBEEF
    public ulong Timestamp;      // milliseconds since epoch
    public uint ConnectionId;    // unique ID for this connection
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct HandshakeResponse
{
    public uint Magic;           // 0xBEEFDEAD
    public uint ConnectionId;    // echo back the connection ID
}

public class NamedPipeGameDataReader : IDisposable
{
    private const string PIPE_NAME = "NexusTKGameData";
    private const int MESSAGE_SIZE = 64; // sizeof(GameDataMessage)
    
    private NamedPipeClientStream? _pipeClient;
    private bool _disposed = false;
    private readonly object _connectionLock = new object();
    private uint _lastSequenceNumber = 0;
    private Thread? _readThread;
    private volatile bool _shouldStop = false;
    private Thread? _heartbeatThread;
    private volatile bool _shouldSendHeartbeats = false;
    private readonly DebugLogService? _debugLog;
    
    // add event for new game data
    public event EventHandler<(bool Success, int MapId, string MapName, int X, int Y, string CharacterName)>? GameDataRead;

    public NamedPipeGameDataReader(DebugLogService? debugLog = null)
    {
        _debugLog = debugLog;
        _debugLog?.LogNamedPipe($"Initializing NamedPipeGameDataReader for pipe '{PIPE_NAME}'");
        
        // start dedicated read thread for blocking reads
        _readThread = new Thread(ReadThreadLoop)
        {
            IsBackground = true,
            Name = "NamedPipeReader"
        };
        _readThread.Start();
        _debugLog?.LogNamedPipe("Started named pipe reader thread");
        
        _heartbeatThread = new Thread(HeartbeatThreadLoop)
        {
            IsBackground = true,
            Name = "NamedPipeHeartbeat"
        };
        _heartbeatThread.Start();
        _debugLog?.LogNamedPipe("Started named pipe heartbeat thread");
    }

    private void ReadThreadLoop()
    {
        while (!_shouldStop && !_disposed)
        {
            try
            {
                // Try to read a message
                var result = ReadMessage();
                if (result.HasValue)
                {
                    var msg = result.Value;
                    _debugLog?.LogNamedPipe($"Received message type {msg.MessageType}, sequence {msg.SequenceNumber}");
                    
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
                        continue;
                    }
                    
                    if (msg.MessageType == MessageType.GameData && msg.IsSuccess && msg.IsPositionValid)
                    {
                        // fire event with game data
                        GameDataRead?.Invoke(this, (true, msg.MapId, msg.MapName, msg.X, msg.Y, msg.CharacterName));
                    }
                    else
                    {
                        // fire event indicating failure
                        GameDataRead?.Invoke(this, (false, 0, string.Empty, 0, 0, "Player"));
                    }
                }
                else
                {
                    // no message available - this is normal, don't reconnect
                    // only reconnect if the connection is actually broken
                    if (_pipeClient == null || !_pipeClient.IsConnected)
                    {
                        _debugLog?.LogNamedPipe("Connection lost, attempting to reconnect...");
                        GameDataRead?.Invoke(this, (false, 0, string.Empty, 0, 0, "Player"));
                        TryConnect();
                        Thread.Sleep(1000);
                    }
                    else
                    {
                        // connection is fine, just no data - wait briefly
                        Thread.Sleep(100);
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLog?.LogNamedPipe($"Read thread error: {ex.Message}");
                TryConnect(); // Try to reconnect
                Thread.Sleep(1000);
            }
        }
    }

    private void HeartbeatThreadLoop()
    {
        byte[] heartbeat = new byte[] { 0xFF }; // simple keepalive byte
        
        while (!_shouldStop && !_disposed)
        {
            if (_shouldSendHeartbeats && _pipeClient != null && _pipeClient.IsConnected)
            {
                try
                {
                    _pipeClient.Write(heartbeat, 0, 1);
                    _pipeClient.Flush();
                    _debugLog?.LogNamedPipe("Heartbeat sent");
                }
                catch (Exception ex)
                {
                    _debugLog?.LogNamedPipe($"Heartbeat failed: {ex.Message}");
                    _shouldSendHeartbeats = false;
                }
            }
            
            Thread.Sleep(5000); // every 5 seconds is enough
        }
    }

    private void TryConnect()
    {
        // clean up existing connection
        _shouldSendHeartbeats = false;
        if (_pipeClient != null)
        {
            try { _pipeClient.Dispose(); } catch { }
            _pipeClient = null;
        }

        try
        {
            _debugLog?.LogNamedPipe($"Attempting to connect to '{PIPE_NAME}'...");
            _pipeClient = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.InOut, PipeOptions.None);
            _pipeClient.Connect(500);
            _debugLog?.LogNamedPipe($"Connected, waiting for server challenge...");
            
            // STEP 1: Read challenge from server
            byte[] challengeBuffer = new byte[16]; // sizeof(ConnectionHandshake)
            int bytesRead = _pipeClient.Read(challengeBuffer, 0, 16);
            
            if (bytesRead != 16)
            {
                throw new Exception($"Invalid challenge size: {bytesRead}");
            }
            
            // parse challenge
            uint magic = BitConverter.ToUInt32(challengeBuffer, 0);
            ulong timestamp = BitConverter.ToUInt64(challengeBuffer, 4);
            uint connectionId = BitConverter.ToUInt32(challengeBuffer, 12);
            
            if (magic != 0xDEADBEEF)
            {
                throw new Exception($"Invalid challenge magic: 0x{magic:X}");
            }
            
            _debugLog?.LogNamedPipe($"Received challenge with ID: {connectionId}");
            
            // STEP 2: Send response
            var response = new HandshakeResponse
            {
                Magic = 0xBEEFDEAD,
                ConnectionId = connectionId
            };
            
            byte[] responseBuffer = new byte[8];
            BitConverter.GetBytes(response.Magic).CopyTo(responseBuffer, 0);
            BitConverter.GetBytes(response.ConnectionId).CopyTo(responseBuffer, 4);
            
            _pipeClient.Write(responseBuffer, 0, responseBuffer.Length);
            _pipeClient.Flush();
            
            _debugLog?.LogNamedPipe($"Sent response, connection established");
            _shouldSendHeartbeats = true;
        }
        catch (Exception ex)
        {
            _debugLog?.LogNamedPipe($"Connection failed: {ex.Message}");
            if (_pipeClient != null)
            {
                try { _pipeClient.Dispose(); } catch { }
                _pipeClient = null;
            }
        }
    }

    private GameDataMessage? ReadMessage()
    {
        if (_pipeClient == null || !_pipeClient.IsConnected)
        {
            return null;
        }

        try
        {            
            byte[] buffer = new byte[MESSAGE_SIZE];
            int bytesRead = _pipeClient.Read(buffer, 0, MESSAGE_SIZE);
            
            if (bytesRead == 0)
            {
                // no data available right now - this is normal for named pipes
                return null;
            }
            
            if (bytesRead != MESSAGE_SIZE)
            {
                _debugLog?.LogNamedPipe($"Partial read: {bytesRead} bytes (expected {MESSAGE_SIZE})");
                return null;
            }
            
            // parse the message
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
            _debugLog?.LogNamedPipe($"ReadMessage error: {ex.Message}");
            return null;
        }
    }

    private void HandleDisconnection()
    {
        lock (_connectionLock)
        {
            if (_pipeClient != null)
            {
                try
                {
                    _pipeClient.Close();
                    _pipeClient.Dispose();
                }
                catch { }
                _pipeClient = null;
            }
        }
    }

    // main method used by ViewModel to read the current game state
    // this is for compatibility with existing code - the timer-based approach is preferred
    public (bool Success, int MapId, string MapName, int X, int Y, string CharacterName) ReadPositionAndName()
    {
        // for named pipe implementation, we rely on the timer callback
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
                _shouldSendHeartbeats = false;
                _readThread?.Join();
                _heartbeatThread?.Join();
                HandleDisconnection();
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