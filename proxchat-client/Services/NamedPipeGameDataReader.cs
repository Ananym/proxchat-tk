using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ProxChatClient.Services;

public class NamedPipeGameDataReader : IDisposable
{
    private const string PIPE_NAME = "NexusTKGameData";
    private const int MESSAGE_SIZE = 64; // sizeof(GameDataMessage)
    
    private NamedPipeClientStream? _pipeClient;
    private bool _disposed = false;
    private readonly object _connectionLock = new object();
    private bool _isConnected = false;
    private uint _lastSequenceNumber = 0;
    private Thread? _readThread;
    private volatile bool _shouldStop = false;
    private readonly DebugLogService? _debugLog;
    
    // connection state tracking for logging
    private bool _hasLoggedInitialConnection = false;
    private DateTime _lastConnectionAttempt = DateTime.MinValue;
    private int _consecutiveConnectionFailures = 0;
    
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
    }

    private void ReadThreadLoop()
    {
        while (!_shouldStop && !_disposed)
        {
            try
            {
                EnsureConnected();
                
                if (_isConnected && _pipeClient != null)
                {
                    var result = ReadMessage();
                    if (result.HasValue)
                    {
                        var msg = result.Value;
                        
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
                }
                else
                {
                    // not connected, wait and retry
                    Thread.Sleep(1000);
                    
                    // fire failure event occasionally to keep UI updated
                    GameDataRead?.Invoke(this, (false, 0, string.Empty, 0, 0, "Player"));
                }
            }
            catch (Exception ex)
            {
                _debugLog?.LogNamedPipe($"NamedPipeGameDataReader read thread error: {ex.Message}");
                HandleDisconnection();
                Thread.Sleep(1000);
            }
        }
    }

    private void EnsureConnected()
    {
        lock (_connectionLock)
        {
            if (_isConnected && _pipeClient?.IsConnected == true)
            {
                return; // already connected
            }

            var now = DateTime.UtcNow;
            
            // cleanup existing connection
            if (_pipeClient != null)
            {
                try
                {
                    _pipeClient.Close();
                    _pipeClient.Dispose();
                }
                catch { }
                _pipeClient = null;
                _isConnected = false;
            }

            // throttle connection attempts - only try every 5 seconds
            if (now - _lastConnectionAttempt < TimeSpan.FromSeconds(5))
            {
                return;
            }
            _lastConnectionAttempt = now;

            try
            {
                _debugLog?.LogNamedPipe($"Attempting to connect to named pipe '{PIPE_NAME}'...");
                
                // Use PipeAccessRights constructor to get ReadData + WriteAttributes access needed for setting ReadMode
                _pipeClient = new NamedPipeClientStream(".", PIPE_NAME, 
                    System.IO.Pipes.PipeAccessRights.ReadData | System.IO.Pipes.PipeAccessRights.WriteAttributes,
                    PipeOptions.None, 
                    System.Security.Principal.TokenImpersonationLevel.None, 
                    HandleInheritability.None);
                _debugLog?.LogNamedPipe($"Created pipe client with ReadData+WriteAttributes access, attempting to connect...");
                
                _pipeClient.Connect(5000);
                _debugLog?.LogNamedPipe($"Connected to pipe successfully, setting read mode to Message...");
                
                // Now we can set ReadMode because we have WriteAttributes access right
                _pipeClient.ReadMode = PipeTransmissionMode.Message;
                _debugLog?.LogNamedPipe($"Successfully set pipe read mode to Message");
                
                _isConnected = true;
                _consecutiveConnectionFailures = 0;
                
                if (!_hasLoggedInitialConnection)
                {
                    _debugLog?.LogNamedPipe($"Successfully connected to named pipe '{PIPE_NAME}'");
                    _hasLoggedInitialConnection = true;
                }
            }
            catch (TimeoutException)
            {
                // server not available, this is expected
                _consecutiveConnectionFailures++;
                if (_consecutiveConnectionFailures == 1 || _consecutiveConnectionFailures % 12 == 0) // log first attempt and every minute
                {
                    _debugLog?.LogNamedPipe($"Named pipe '{PIPE_NAME}' connection timeout - server not available (attempt {_consecutiveConnectionFailures})");
                }
                
                _pipeClient?.Dispose();
                _pipeClient = null;
                _isConnected = false;
            }
            catch (Exception ex)
            {
                _consecutiveConnectionFailures++;
                _debugLog?.LogNamedPipe($"Named pipe connection failed: {ex.Message} (attempt {_consecutiveConnectionFailures})");
                
                _pipeClient?.Dispose();
                _pipeClient = null;
                _isConnected = false;
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
            _debugLog?.LogNamedPipe($"Starting to read message from pipe in message mode");
            
            // for message mode pipes, read the entire message in one call
            // the server sends exactly MESSAGE_SIZE bytes per message
            byte[] buffer = new byte[MESSAGE_SIZE];
            
            int bytesRead = _pipeClient.Read(buffer, 0, MESSAGE_SIZE);
            if (bytesRead == 0)
            {
                _debugLog?.LogNamedPipe($"Pipe read returned 0 bytes - pipe closed");
                return null;
            }
            
            if (bytesRead != MESSAGE_SIZE)
            {
                _debugLog?.LogNamedPipe($"Message mode read returned {bytesRead} bytes, expected {MESSAGE_SIZE}");
                // in message mode, we should get the complete message or nothing
                return null;
            }
            
            _debugLog?.LogNamedPipe($"Successfully read complete message ({bytesRead} bytes), parsing...");
            
            // manually parse the struct to avoid marshalling issues
            var msg = new GameDataMessage();
            
            // parse header (8 bytes)
            msg.MessageType = BitConverter.ToUInt32(buffer, 0);
            msg.SequenceNumber = BitConverter.ToUInt32(buffer, 4);
            _debugLog?.LogNamedPipe($"Parsed header: MessageType={msg.MessageType}, SequenceNumber={msg.SequenceNumber}");
            
            // parse timestamp (8 bytes)
            msg.TimestampMs = BitConverter.ToUInt64(buffer, 8);
            _debugLog?.LogNamedPipe($"Parsed timestamp: {msg.TimestampMs}");
            
            // parse game data (40 bytes)
            msg.X = BitConverter.ToInt32(buffer, 16);
            msg.Y = BitConverter.ToInt32(buffer, 20);
            msg.MapId = BitConverter.ToUInt16(buffer, 24);
            msg.Reserved1 = BitConverter.ToUInt16(buffer, 26);
            _debugLog?.LogNamedPipe($"Parsed game data: X={msg.X}, Y={msg.Y}, MapId={msg.MapId}");
            
            // parse strings (28 bytes)
            msg.MapNameBytes = new byte[16];
            msg.CharacterNameBytes = new byte[12];
            Array.Copy(buffer, 28, msg.MapNameBytes, 0, 16);
            Array.Copy(buffer, 44, msg.CharacterNameBytes, 0, 12);
            _debugLog?.LogNamedPipe($"Parsed strings: MapName='{msg.MapName}', CharacterName='{msg.CharacterName}'");
            
            // parse flags (8 bytes)
            msg.Flags = BitConverter.ToUInt32(buffer, 56);
            msg.Reserved2 = BitConverter.ToUInt32(buffer, 60);
            _debugLog?.LogNamedPipe($"Parsed flags: Flags=0x{msg.Flags:X8}, Reserved2=0x{msg.Reserved2:X8}");
            
            _debugLog?.LogNamedPipe($"Message parsing completed successfully");
            return msg;
        }
        catch (IOException ex) when (ex.Message.Contains("pipe has been ended"))
        {
            _debugLog?.LogNamedPipe($"Pipe ended during read: {ex.Message}");
            HandleDisconnection();
            return null;
        }
        catch (Exception ex)
        {
            _debugLog?.LogNamedPipe($"NamedPipeGameDataReader: Read error: {ex.Message}");
            HandleDisconnection();
            return null;
        }
    }

    private void HandleDisconnection()
    {
        lock (_connectionLock)
        {
            _isConnected = false;
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
                _readThread?.Join();
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