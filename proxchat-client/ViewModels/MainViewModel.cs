using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks; // Added for Task
using System.Windows.Input;
using ProxChatClient.Models;
using ProxChatClient.Services;
using ProxChatClient.Models.Signaling;
using System.Diagnostics;
using System.Threading; // Added for Timer
using ProxChatClient.Converters; // Assuming converters are here or adjust namespace
using System.Windows; // For Application.Current
using System.IO; // For Path
using System.Text.Json; // For JSON serialization if not already used
using System.Collections.Concurrent; // Added for ConcurrentDictionary
// Ensure RelayCommand namespace is covered - assuming it's within ViewModels or a sub-namespace
// using ProxChatClient.Commands; // Example if it were in a ProxChatClient.Commands namespace

namespace ProxChatClient.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly GameDataReader _memoryReader;
    private readonly SignalingService _signalingService;
    private readonly WebRtcService _webRtcService;
    private readonly AudioService _audioService;
    private readonly Config _config;
    private string _statusMessage = "Initializing..."; // Start with initializing status
    private bool _isRunning;
    private float _audioLevel;
    private bool _isPushToTalk;
    private Key _pushToTalkKey = Key.V;
    private bool _isEditingPushToTalk;
    private Key _muteSelfKey = Key.M; // Add default key
    private bool _isEditingMuteSelf; // Add editing state
    private float _volumeScale = 1.0f;
    private float _inputVolumeScale = 1.0f;
    private float _minBroadcastThreshold = 0.0f;
    private Timer? _positionSendTimer; // Renamed from _positionTimer for clarity
    private Timer? _uiUpdateTimer; // New timer for UI updates
    private bool _isMemoryReaderInitialized = false; // Flag for memory reader status

    // Debug Mode Fields
    private bool _isDebugModeEnabled = false;
    private string _debugCharacterName = Guid.NewGuid().ToString().Substring(0, 8); // Default random string
    private int _debugX = 110; // Changed to int
    private int _debugY = 206; // Changed to int
    private int _debugMapId = 0;
    private bool _useMp3Input = false;

    // Fields to track last sent position for conditional updates
    private int? _lastSentMapId;
    private int _lastSentX; // Changed to int
    private int _lastSentY; // Changed to int
    private DateTime _lastSentTime = DateTime.MinValue;
    private const int PositionTolerance = 0; // Changed to int, 0 for exact match with integers
    private readonly TimeSpan _forceSendInterval = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _positionSendInterval = TimeSpan.FromMilliseconds(250); // Changed from 1 second to 250ms for more responsive updates
    private readonly TimeSpan _uiUpdateInterval = TimeSpan.FromMilliseconds(200); // Interval for UI updates (e.g., 5 times/sec)

    // New properties for UI binding
    private int _currentMapId = 0; // Changed to int, default 0
    private string _currentMapName = "N/A"; // Added for map name display
    private int _currentX = 0; // Changed to int
    private int _currentY = 0; // Changed to int
    private string _currentCharacterName = "Player"; // Add current character name field

    private readonly string _peerSettingsFilePath; // Added for dedicated peer settings file

    // Add this field with the other private fields around line 30
    private DateTime _lastSuccessfulReadTime = DateTime.MinValue;
    private readonly TimeSpan _gameDataTimeout = TimeSpan.FromSeconds(5);

    // Add this field with the other private fields around line 30
    private readonly DebugLogService _debugLog;

    // Add a dictionary to track pending peers (not yet in UI)
    private readonly ConcurrentDictionary<string, bool> _pendingPeers = new(); 

    public ObservableCollection<PeerViewModel> ConnectedPeers { get; } = new();
    public ObservableCollection<string> InputDevices => _audioService.InputDevices;
    
    // Debug Mode Properties
    public bool IsDebugModeEnabled
    {
        get => _isDebugModeEnabled;
        set
        {
            if (_isDebugModeEnabled != value)
            {
                _isDebugModeEnabled = value;
                OnPropertyChanged();
                // Refresh UI-bound properties that depend on debug mode
                OnPropertyChanged(nameof(CurrentCharacterName));
                OnPropertyChanged(nameof(CurrentX));
                OnPropertyChanged(nameof(CurrentY));
                OnPropertyChanged(nameof(CurrentMapId));
                OnPropertyChanged(nameof(CurrentMapName)); // Map name might also be affected or need a debug version
                // If debug mode is turned on, update the UI immediately with debug values
                if (_isDebugModeEnabled)
                {
                    UpdateUiWithDebugValues();
                }
                else
                {
                    // If turning off debug mode, re-initialize or re-read from game
                    // For now, just trigger an update which will attempt to read from memory
                    UpdateUiPosition(null);
                }
            }
        }
    }

    public string DebugCharacterName
    {
        get => _debugCharacterName;
        set { _debugCharacterName = value; OnPropertyChanged(); if (IsDebugModeEnabled) OnPropertyChanged(nameof(CurrentCharacterName)); }
    }

    public int DebugX // Changed to int
    {
        get => _debugX;
        set { _debugX = value; OnPropertyChanged(); if (IsDebugModeEnabled) OnPropertyChanged(nameof(CurrentX)); }
    }

    public int DebugY // Changed to int
    {
        get => _debugY;
        set { _debugY = value; OnPropertyChanged(); if (IsDebugModeEnabled) OnPropertyChanged(nameof(CurrentY)); }
    }

    public int DebugMapId
    {
        get => _debugMapId;
        set { _debugMapId = value; OnPropertyChanged(); if (IsDebugModeEnabled) OnPropertyChanged(nameof(CurrentMapId)); }
    }

    // Debug Mode Commands
    public ICommand IncrementDebugXCommand { get; }
    public ICommand DecrementDebugXCommand { get; }
    public ICommand IncrementDebugYCommand { get; }
    public ICommand DecrementDebugYCommand { get; }

    public string? SelectedInputDevice
    {
        get => _audioService.SelectedInputDevice;
        set
        {
            _audioService.SelectedInputDevice = value;
            OnPropertyChanged();
        }
    }

    public float AudioLevel
    {
        get => _audioLevel;
        private set
        {
            _audioLevel = value;
            OnPropertyChanged();
        }
    }

    public float InputVolumeScale
    {
        get => _inputVolumeScale;
        set
        {
            _inputVolumeScale = value;
            _audioService.SetInputVolumeScale(value);
            OnPropertyChanged();
        }
    }

    public float VolumeScale
    {
        get => _volumeScale;
        set
        {
            _volumeScale = value;
            _audioService.SetOverallVolumeScale(value);
            OnPropertyChanged();
        }
    }

    public bool IsPushToTalk
    {
        get => _isPushToTalk;
        set
        {
            if (_isPushToTalk != value)
            {
                _isPushToTalk = value;
                _audioService.SetPushToTalk(value); 
                OnPropertyChanged();
                if (!value) IsEditingPushToTalk = false; 
                ((RelayCommand)EditPushToTalkCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public Key PushToTalkKey
    {
        get => _pushToTalkKey;
        set
        {
            if (_pushToTalkKey != value)
            {
                _pushToTalkKey = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsEditingPushToTalk
    {
        get => _isEditingPushToTalk;
        set
        {
             if (_isEditingPushToTalk != value)
             {
                 _isEditingPushToTalk = value;
                 OnPropertyChanged();
                 ((RelayCommand)EditPushToTalkCommand).RaiseCanExecuteChanged();
             }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            _isRunning = value;
            OnPropertyChanged();
            ((RelayCommand)StartCommand).RaiseCanExecuteChanged();
            ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
        }
    }
    
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelfMuted => _audioService.IsSelfMuted;

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand EditPushToTalkCommand { get; }
    public ICommand RefreshDevicesCommand { get; }
    public ICommand ToggleSelfMuteCommand { get; }
    public ICommand EditMuteSelfCommand { get; }

    // Updated property type to int
    public int CurrentMapId
    {
        get => IsDebugModeEnabled ? _debugMapId : _currentMapId;
        set 
        {
            if (IsDebugModeEnabled) DebugMapId = value;
            else _currentMapId = value; 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(CurrentMapDisplay)); 
        }
    }
    // Added property for map name
    public string CurrentMapName
    {
        get => IsDebugModeEnabled ? $"DebugMap ({_debugMapId})" : _currentMapName; // Provide a debug map name
        set 
        { 
            // Assuming map name isn't directly settable in debug mode, or linked to DebugMapId
            if (!IsDebugModeEnabled) _currentMapName = value; 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(CurrentMapDisplay)); 
        }
    }

    // Combined display property
    public string CurrentMapDisplay => IsDebugModeEnabled ? $"DebugMap ({DebugMapId})" : (IsRunning || _isMemoryReaderInitialized ? $"{_currentMapName} ({_currentMapId})" : "Waiting...");

    public int CurrentX // Changed to int
    {
        get => IsDebugModeEnabled ? _debugX : _currentX;
        set 
        {
            if (IsDebugModeEnabled) DebugX = value;
            else _currentX = value; 
            OnPropertyChanged(); 
        }
    }
    public int CurrentY // Changed to int
    {
        get => IsDebugModeEnabled ? _debugY : _currentY;
        set 
        {
            if (IsDebugModeEnabled) DebugY = value;
            else _currentY = value; 
            OnPropertyChanged(); 
        }
    }
    public string CurrentCharacterName
    {
        get => IsDebugModeEnabled ? _debugCharacterName : _currentCharacterName;
        set 
        { 
            if (IsDebugModeEnabled) DebugCharacterName = value;
            else _currentCharacterName = value; 
            OnPropertyChanged(); 
        }
    }

    public bool UseMp3Input
    {
        get => _useMp3Input;
        set
        {
            if (_useMp3Input != value)
            {
                _useMp3Input = value;
                _audioService.UseMp3Input = value;
                OnPropertyChanged();
            }
        }
    }

    public float MinBroadcastThreshold
    {
        get => _minBroadcastThreshold;
        set
        {
            if (Math.Abs(_minBroadcastThreshold - value) > 0.001f)
            {
                _minBroadcastThreshold = value;
                _audioService.MinBroadcastThreshold = value;
                OnPropertyChanged();
            }
        }
    }

    public Key MuteSelfKey
    {
        get => _muteSelfKey;
        set
        {
            if (_muteSelfKey != value)
            {
                _muteSelfKey = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsEditingMuteSelf
    {
        get => _isEditingMuteSelf;
        set
        {
            if (_isEditingMuteSelf != value)
            {
                _isEditingMuteSelf = value;
                OnPropertyChanged();
                ((RelayCommand)EditMuteSelfCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public MainViewModel(Config config)
    {
        _config = config;
        // Define path for peer settings, e.g., in user's app data or alongside main config
        _peerSettingsFilePath = Path.Combine(AppContext.BaseDirectory, "PeerSettings.json");
        LoadPeerSettings(); // Load settings on startup

        _memoryReader = new GameDataReader();
        _audioService = new AudioService(config.AudioSettings.MaxDistance, config);
        _signalingService = new SignalingService(config.WebSocketServer);
        var debugLog = new DebugLogService(); // Will use command line args
        _debugLog = debugLog; // Store reference for MainViewModel use
        _webRtcService = new WebRtcService(_audioService, _signalingService, config.AudioSettings.MaxDistance, debugLog);
        
        StartCommand = new RelayCommand(Start, () => !_isRunning);
        StopCommand = new RelayCommand(Stop, () => _isRunning);
        ToggleMuteCommand = new RelayCommand<string>(TogglePeerMute);
        EditPushToTalkCommand = new RelayCommand(() => { IsEditingPushToTalk = !IsEditingPushToTalk; }, () => IsPushToTalk);
        RefreshDevicesCommand = new RelayCommand(_audioService.RefreshInputDevices);
        ToggleSelfMuteCommand = new RelayCommand(ToggleSelfMute);
        EditMuteSelfCommand = new RelayCommand(() => { IsEditingMuteSelf = !IsEditingMuteSelf; });

        // Initialize Debug Commands
        IncrementDebugXCommand = new RelayCommand(() => DebugX++);
        DecrementDebugXCommand = new RelayCommand(() => DebugX--);
        IncrementDebugYCommand = new RelayCommand(() => DebugY++);
        DecrementDebugYCommand = new RelayCommand(() => DebugY--);

        _signalingService.NearbyClientsReceived += HandleNearbyClients;
        _signalingService.ConnectionStatusChanged += HandleSignalingConnectionStatus;
        _signalingService.SignalingErrorReceived += HandleSignalingError;
        _webRtcService.PositionReceived += HandlePeerPosition;
        _webRtcService.DataChannelOpened += HandleDataChannelOpened;
        _audioService.AudioLevelChanged += (_, level) => AudioLevel = level;
        _audioService.RefreshedDevices += (s, e) => { 
            OnPropertyChanged(nameof(InputDevices));
            var currentSelection = SelectedInputDevice;
            SelectedInputDevice = (currentSelection != null && _audioService.InputDevices.Contains(currentSelection)) 
                                ? currentSelection 
                                : _audioService.InputDevices.FirstOrDefault();
        };

        RefreshDevicesCommand.Execute(null);

        _positionSendTimer = new Timer(SendPositionUpdate, null, Timeout.Infinite, Timeout.Infinite);
        _uiUpdateTimer = new Timer(UpdateUiPosition, null, TimeSpan.Zero, _uiUpdateInterval);
    }

    private void LoadPeerSettings()
    {
        try
        {
            if (File.Exists(_peerSettingsFilePath))
            {
                string json = File.ReadAllText(_peerSettingsFilePath);
                var loadedSettings = JsonSerializer.Deserialize<Dictionary<string, PeerPersistentState>>(json);
                if (loadedSettings != null)
                {
                    // We don't directly assign to _config.PeerSettings here if Config manages its own loading.
                    // Instead, we'll keep them separate for this example or merge them if Config is the sole source of truth.
                    // For now, let's assume _config.PeerSettings is the live one and we populate it.
                    _config.PeerSettings = loadedSettings;
                    Debug.WriteLine($"Loaded {loadedSettings.Count} peer settings from {_peerSettingsFilePath}");
                }
            }
            else
            {
                _config.PeerSettings = new Dictionary<string, PeerPersistentState>();
                Debug.WriteLine($"Peer settings file not found at {_peerSettingsFilePath}. Initializing new settings.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading peer settings: {ex.Message}");
            _config.PeerSettings = new Dictionary<string, PeerPersistentState>(); // Initialize to empty on error
        }
    }

    private void SavePeerSettings()
    {
        try
        {
            string json = JsonSerializer.Serialize(_config.PeerSettings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_peerSettingsFilePath, json);
            Debug.WriteLine($"Saved peer settings to {_peerSettingsFilePath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving peer settings: {ex.Message}");
        }
    }

    private void ApplyPersistedSettings(PeerViewModel peerVm)
    {
        if (peerVm == null || string.IsNullOrEmpty(peerVm.CharacterName) || peerVm.CharacterName == PeerViewModel.DefaultCharacterName)
        {
            Debug.WriteLine($"ApplyPersistedSettings: PeerVM is null or CharacterName is default/empty for ID {peerVm?.Id}. Skipping.");
            return;
        }

        if (_config.PeerSettings.TryGetValue(peerVm.CharacterName, out var settings))
        {
            Debug.WriteLine($"ApplyPersistedSettings: Found settings for {peerVm.CharacterName}. Volume: {settings.Volume}, Muted: {settings.IsMuted}");
            // Set ViewModel properties. Setters will handle AudioService & persistence if values actually change.
            peerVm.Volume = settings.Volume;
            peerVm.IsMuted = settings.IsMuted;
        }
        else
        {
            Debug.WriteLine($"ApplyPersistedSettings: No saved settings for {peerVm.CharacterName}. Applying defaults & saving.");
            // If no settings, apply defaults from PeerViewModel and then save them to create an entry.
            // This ensures that a new known peer gets their initial (default) state persisted.
            UpdateAndSavePeerSetting(peerVm.CharacterName, peerVm.Volume, peerVm.IsMuted); 
        }
    }
    
    private void UpdateAndSavePeerSetting(string characterName, float volume, bool isMuted)
    {
        if (string.IsNullOrEmpty(characterName) || characterName == PeerViewModel.DefaultCharacterName) return;

        bool changed = false;
        if (_config.PeerSettings.TryGetValue(characterName, out var existingSettings))
        {
            // Check if anything actually changed
            if (Math.Abs(existingSettings.Volume - volume) > 0.001f || existingSettings.IsMuted != isMuted)
            {
                existingSettings.Volume = volume;
                existingSettings.IsMuted = isMuted;
                changed = true;
            }
        }
        else
        {
            // New entry, always save
            _config.PeerSettings[characterName] = new PeerPersistentState { Volume = volume, IsMuted = isMuted };
            changed = true;
        }

        if (changed)
        {
            SavePeerSettings();
            Debug.WriteLine($"Updated and saved setting for {characterName}: Vol={volume}, Mute={isMuted}");
        }
        else
        {
            Debug.WriteLine($"Setting for {characterName} not changed. Vol={volume}, Mute={isMuted}. No save needed.");
        }
    }

    private void InitializeMemoryReader()
    {
        StatusMessage = "Attempting to establish connection with game data provider...";
        // _isMemoryReaderInitialized = _memoryReader.Initialize(); // Removed - Initialization is now implicit
        // Check initial state by attempting a read
        // We don't set _isMemoryReaderInitialized here directly, 
        // it gets set effectively by the success/failure of the first read in UpdateUiPosition
        UpdateUiPosition(null); // Attempt initial read to check status and get initial position
        
        // Status message will be updated based on the result of UpdateUiPosition
        // If UpdateUiPosition fails to open the MMF, it will return defaults and log an error.
        // If it succeeds, it updates the UI.
    }

    // New method for UI Timer callback
    private void UpdateUiPosition(object? state)
    {
        if (IsDebugModeEnabled)
        {
            // In debug mode, UI is driven by debug properties directly.
            StatusMessage = "Debug Mode Active. Game data is being overridden.";
            return;
        }

        try
        {
            // Assuming ReadPosition now returns name as well
            // Tuple now returns (int MapId, string MapName, ...)
            var (mapId, mapName, x, y, name) = _memoryReader.ReadPositionAndName();

            // Check if the read was successful (mapId is not the default 0, or handle empty string mapName if that's a better indicator)
            bool currentReadSuccess = mapId != 0; // Assuming mapId 0 indicates failure/not ready

            if (currentReadSuccess)
            {
                _lastSuccessfulReadTime = DateTime.UtcNow; // Update last successful read time
                
                if (!_isMemoryReaderInitialized)
                {
                    // First successful read
                    _isMemoryReaderInitialized = true;
                    StatusMessage = "Game data connection established. Ready.";
                    Debug.WriteLine("GameMemoryReader successfully connected to MMF.");
                }
            }
            else if (!currentReadSuccess && _isMemoryReaderInitialized)
            {
                // Lost connection after it was previously established
                _isMemoryReaderInitialized = false;
                StatusMessage = "Lost connection to game data provider. Waiting...";
                Debug.WriteLine("GameMemoryReader lost connection to MMF.");
            }

            // Check for timeout and auto-disconnect if conditions are met
            if (IsRunning && !IsDebugModeEnabled && _lastSuccessfulReadTime != DateTime.MinValue)
            {
                var timeSinceLastRead = DateTime.UtcNow - _lastSuccessfulReadTime;
                if (timeSinceLastRead > _gameDataTimeout)
                {
                    Debug.WriteLine($"Game data timeout exceeded ({timeSinceLastRead.TotalSeconds:F1}s). Auto-disconnecting...");
                    _ = Task.Run(async () => await StopAsync()); // Call stop asynchronously to avoid blocking the timer
                    return; // Exit early, no need to update UI
                }
            }
            
            // Update UI properties regardless of connection status (show defaults/error on failure)
            App.Current.Dispatcher.Invoke(() =>
            {
                CurrentMapId = currentReadSuccess ? mapId : 0; // Set int MapId
                CurrentMapName = currentReadSuccess ? mapName : "N/A"; // Set MapName
                CurrentX = x;
                CurrentY = y;
                CurrentCharacterName = currentReadSuccess ? name : "Player"; 
            });
        }
        catch (Exception ex)
        {
            // Log error or update status if reading fails persistently
            Debug.WriteLine($"Error reading game position for UI: {ex.Message}");
            // Indicate error state in UI
            _isMemoryReaderInitialized = false; // Assume connection is lost on exception
            App.Current.Dispatcher.Invoke(() =>
            {
                StatusMessage = "Error reading game data.";
                CurrentMapId = 0;
                CurrentX = 0; // Set int X
                CurrentY = 0; // Set int Y
                CurrentCharacterName = "Player";
            });
        }
    }

    private void UpdateUiWithDebugValues()
    {
        OnPropertyChanged(nameof(CurrentCharacterName));
        OnPropertyChanged(nameof(CurrentX));
        OnPropertyChanged(nameof(CurrentY));
        OnPropertyChanged(nameof(CurrentMapId));
        OnPropertyChanged(nameof(CurrentMapName));
        OnPropertyChanged(nameof(CurrentMapDisplay));
        StatusMessage = "Debug Mode Active. Game data is being overridden.";
    }

    private async void Start()
    {
        if (string.IsNullOrEmpty(SelectedInputDevice))
        {
            StatusMessage = "Error: No audio device selected.";
            return;
        }
        
        try
        {
            StatusMessage = "Connecting to signaling server...";
            try
            {
                await _signalingService.Connect();
            }
            catch (SignalingConnectionException ex)
            {
                StatusMessage = $"Failed to connect to signaling server: {ex.Message}";
                Debug.WriteLine($"Signaling connection failed: {ex}");
                return;
            }

            if (!_signalingService.IsConnected) 
            {
                StatusMessage = "Error: Failed to connect to signaling server.";
                return; 
            }
            
            try
            {
                StatusMessage = "Starting audio capture...";
                _audioService.StartCapture();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error starting audio capture: {ex.Message}";
                Debug.WriteLine($"Audio capture start failed: {ex}");
                await StopAsync();
                return;
            }
            
            // Reset last sent state
            _lastSentMapId = null;
            _lastSentTime = DateTime.MinValue;
            
            // Reset game data timeout tracking
            _lastSuccessfulReadTime = DateTime.MinValue;

            _positionSendTimer?.Dispose();
            // Update timer to tick every second FOR SENDING
            _positionSendTimer = new Timer(SendPositionUpdate, null, TimeSpan.Zero, _positionSendInterval); // Use the send interval

            IsRunning = true;
            StatusMessage = "Running";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error starting: {ex.Message}";
            Debug.WriteLine($"Error starting: {ex}");
            await StopAsync(); // Call the async version here
        }
    }

    // Wrapper for the command
    private void Stop()
    { 
        _ = StopAsync(); // Fire-and-forget
    }

    // Actual async implementation
    private async Task StopAsync()
    {
        StatusMessage = "Stopping...";
        _positionSendTimer?.Dispose(); // Stop the sending timer
        _positionSendTimer = null;

        try
        {
            _audioService.StopCapture(); // This might need removal/adjustment if SIPSorcery handles capture
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error stopping audio capture: {ex}");
        }

        try
        {
            _webRtcService.CloseAllConnections();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error closing WebRTC connections: {ex}");
        }
        
        if (_signalingService.IsConnected)
        {
            try
            {
                await _signalingService.Disconnect(); 
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error disconnecting from signaling server: {ex}");
            }
        }
        
        App.Current.Dispatcher.Invoke(() => ConnectedPeers.Clear());

        IsRunning = false;
        // Keep UI timer running, reset status message
        StatusMessage = _isMemoryReaderInitialized ? "Stopped. Monitoring game position." : "Stopped. Game process not found.";
    }

    private void TogglePeerMute(string? peerId)
    {
        if (peerId == null) return;

        var peerVm = ConnectedPeers.FirstOrDefault(p => p.Id == peerId);
        if (peerVm != null)
        {
            // This will trigger the IsMuted setter in PeerViewModel,
            // which in turn calls UpdatePeerMuteStateFromViewModel in MainViewModel,
            // which then updates AudioService and persists the setting.
            peerVm.IsMuted = !peerVm.IsMuted;
            Debug.WriteLine($"TogglePeerMute command executed for peer {peerId}. New IsMuted (from VM perspective): {peerVm.IsMuted}");
        }
        else
        {
             Debug.WriteLine($"Attempted to toggle mute for unknown peer ID: {peerId}");
        }
    }
    
    public void ToggleSelfMute()
    {
        _audioService.ToggleSelfMute();
        OnPropertyChanged(nameof(IsSelfMuted));
    }

    private async void SendPositionUpdate(object? state)
    {
        if (!IsDebugModeEnabled && !_isMemoryReaderInitialized && !IsRunning) return; 

        try
        {
            int mapId;
            int x;
            int y;
            string name;

            if (IsDebugModeEnabled)
            {
                mapId = _debugMapId;
                x = _debugX;
                y = _debugY;
                name = _debugCharacterName;
            }
            else
            {
                // Read current position and name (mapId is int, mapName is string, x and y are int)
                var gameData = _memoryReader.ReadPositionAndName(); 
                mapId = gameData.MapId;
                x = gameData.X; // Expects int
                y = gameData.Y; // Expects int
                name = gameData.CharacterName;
            }

            var now = DateTime.UtcNow;
            bool mapChanged = mapId != _lastSentMapId;
            bool positionChangedSignificantly = Math.Abs(x - _lastSentX) > PositionTolerance || Math.Abs(y - _lastSentY) > PositionTolerance;
            bool forceSend = (now - _lastSentTime) >= _forceSendInterval;

            if ((mapChanged || positionChangedSignificantly || forceSend) && _signalingService.IsConnected)
            {
                await _signalingService.UpdatePosition(mapId, x, y); 
                _lastSentMapId = mapId;
                _lastSentX = x;
                _lastSentY = y;
                _lastSentTime = now;
            }
            
            // Only send position updates to peers in the UI (not pending peers)
            foreach (var peer in ConnectedPeers)
            {
                _webRtcService.SendPosition(peer.Id, mapId, x, y, name);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error sending position update: {ex.Message}");
            // Consider handling repeated errors, maybe attempt reconnect or stop
        }
    }

    private async void HandleNearbyClients(object? sender, List<string> nearbyClients)
    {
        await App.Current.Dispatcher.InvokeAsync(async () =>
        {
            var currentPeerIds = ConnectedPeers.Select(p => p.Id).ToList();
            var pendingPeerIds = _pendingPeers.Keys.ToList(); // Get all pending peer IDs
            var allKnownPeerIds = currentPeerIds.Concat(pendingPeerIds).Distinct().ToList(); // Combine visible and pending peers
            
            var toRemoveIds = allKnownPeerIds.Except(nearbyClients).ToList();
            var toAddIds = nearbyClients.Except(allKnownPeerIds).ToList();
            var myClientId = _signalingService.ClientId;

            if (myClientId == null)
            {
                Debug.WriteLine("HandleNearbyClients Warning: My ClientId is null.");
                return;
            }

            Debug.WriteLine($"Nearby update: MyId={myClientId}, Current={string.Join(",", currentPeerIds)}, Pending={string.Join(",", pendingPeerIds)}, Nearby={string.Join(",", nearbyClients)}, Add={string.Join(",", toAddIds)}, Remove={string.Join(",", toRemoveIds)}");

            foreach (var peerId in toRemoveIds)
            {
                // Check and remove from UI collection
                var peerVmToRemove = ConnectedPeers.FirstOrDefault(p => p.Id == peerId);
                if (peerVmToRemove != null)
                {
                    // Important: CharacterName might not be known if removed quickly.
                    // We don't save settings on removal here, as they are saved on change.
                    // We do need to clean up audio resources.
                    ConnectedPeers.Remove(peerVmToRemove);
                    Debug.WriteLine($"Removed peer {peerId} (VM: {peerVmToRemove.CharacterName}) from UI.");
                }
                
                // Clean up pending peers
                _pendingPeers.TryRemove(peerId, out _);
                
                // Clean up WebRTC and audio resources regardless of UI state
                _webRtcService.RemovePeerConnection(peerId); // WebRTC cleanup
                _audioService.RemovePeerAudioSource(peerId); // AudioService cleanup
                Debug.WriteLine($"Cleaned up resources for peer {peerId}");
            }

            foreach (var peerId in toAddIds)
            {
                // Don't add to UI yet - add to pending peers and initiate connection
                if (!_pendingPeers.ContainsKey(peerId))
                {
                    bool amInitiator = string.CompareOrdinal(myClientId, peerId) > 0;
                    Debug.WriteLine($"Adding new pending peer {peerId}, Initiator: {amInitiator}");
                    
                    _pendingPeers[peerId] = true;
                    
                    // Create WebRTC connection, but don't add to UI yet
                    // The peer will be added to ConnectedPeers when first position data is received
                    await _webRtcService.CreatePeerConnection(peerId, amInitiator);
                }
            }
        });
    }

    // Handler for the new DataChannelOpened event
    private void HandleDataChannelOpened(object? sender, string peerId)
    {
        _debugLog.LogMain($"DataChannelOpened for peer {peerId} - Sending immediate position update");
        // Immediately send our position data
        Task.Run(() => SendPositionUpdateForPeer(peerId));
    }
    
    // New method to send position update to a specific peer
    private void SendPositionUpdateForPeer(string peerId)
    {
        try
        {
            int mapId;
            int x;
            int y;
            string name;

            if (IsDebugModeEnabled)
            {
                mapId = _debugMapId;
                x = _debugX;
                y = _debugY;
                name = _debugCharacterName;
            }
            else
            {
                // Read current position and name
                var gameData = _memoryReader.ReadPositionAndName(); 
                mapId = gameData.MapId;
                x = gameData.X;
                y = gameData.Y;
                name = gameData.CharacterName;
            }

            _debugLog.LogMain($"Sending position to peer {peerId}: MapId={mapId}, X={x}, Y={y}, Name={name}");
            _webRtcService.SendPosition(peerId, mapId, x, y, name);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error sending position update to peer {peerId}: {ex.Message}");
        }
    }

    private void HandlePeerPosition(object? sender, (string PeerId, int MapId, int X, int Y, string CharacterName) positionData)
    {
        try
        {
            var peerId = positionData.PeerId;
            
            // Check if this is a pending peer that we need to add to the UI
            bool isPending = _pendingPeers.TryRemove(peerId, out _);
            
            // Get existing or create new PeerViewModel
            PeerViewModel? peerVm = ConnectedPeers.FirstOrDefault(p => p.Id == peerId);
            
            // If it's a new peer (pending or unknown)
            if (peerVm == null)
            {
                // This is the first position data we're receiving, so add to UI
                _debugLog.LogMain($"First position data received from {peerId} - adding to UI");
                peerVm = new PeerViewModel { Id = peerId };
                
                App.Current.Dispatcher.Invoke(() => {
                    ConnectedPeers.Add(peerVm);
                });
                _debugLog.LogMain($"Added peer {peerId} to UI after receiving first position data");
            }

            int myMapId;
            int myX;
            int myY;

            if (IsDebugModeEnabled)
            {
                myMapId = _debugMapId;
                myX = _debugX;
                myY = _debugY;
            }
            else
            {
                var gameData = _memoryReader.ReadPositionAndName();
                myMapId = gameData.MapId;
                myX = gameData.X;
                myY = gameData.Y;
            }

            // Update character name if it's new and valid
            bool nameJustAssigned = false;
            if ((peerVm.CharacterName == PeerViewModel.DefaultCharacterName || peerVm.CharacterName != positionData.CharacterName) && 
                !string.IsNullOrEmpty(positionData.CharacterName) && positionData.CharacterName != PeerViewModel.DefaultCharacterName)
            {
                _debugLog.LogMain($"Peer {peerId} character name updated from '{peerVm.CharacterName}' to '{positionData.CharacterName}'.");
                peerVm.CharacterName = positionData.CharacterName;
                nameJustAssigned = true;
            }

            // Apply persisted settings if the name was just assigned or if we haven't applied them yet for this known name
            // This also handles the case where a peer connected, name was known, but MainVM restarted and needs to re-apply.
            if (nameJustAssigned && peerVm.CharacterName != PeerViewModel.DefaultCharacterName)
            {
                ApplyPersistedSettings(peerVm);
            }

            // If on different maps, handle audio appropriately and skip distance-based audio updates
            if (myMapId != positionData.MapId)
            {
                _audioService.UpdatePeerDistance(peerId, float.MaxValue); // Effectively mute or max distance
                peerVm.Distance = float.MaxValue; 
                // We might want to set a specific status or visual indicator for peers on different maps.
                return;
            }

            // Add this debug output right after getting the coordinates in HandlePeerPosition
            _debugLog.LogMain($"HandlePeerPosition Debug - Peer {peerId}:");
            _debugLog.LogMain($"  My position: ({myX}, {myY}) Map: {myMapId}");
            _debugLog.LogMain($"  Peer position: ({positionData.X}, {positionData.Y}) Map: {positionData.MapId}");
            _debugLog.LogMain($"  Character: {positionData.CharacterName}");

            var dx = myX - positionData.X;
            var dy = myY - positionData.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);

            _debugLog.LogMain($"  dx: {dx}, dy: {dy}, distance: {distance}");

            _audioService.UpdatePeerDistance(peerId, distance);
            peerVm.Distance = distance;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error handling peer position for {positionData.PeerId}: {ex.Message}");
        }
    }

    private void HandleSignalingConnectionStatus(object? sender, bool isConnected)
    {
         if (!isConnected && IsRunning)
         {
             StatusMessage = "Disconnected from signaling server. Attempting to reconnect...";
         }
         else if (isConnected && IsRunning)
         {
             StatusMessage = "Running (Reconnected)";
         }
         else if (!isConnected && !IsRunning)
         {
             StatusMessage = "Stopped";
         }
    }
    
    private void HandleSignalingError(object? sender, string errorMessage)
    {
        StatusMessage = $"Signaling Error: {errorMessage}";
        Trace.TraceError($"Signaling Error: {errorMessage}");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        });
    }

    public void Dispose()
    {
        _positionSendTimer?.Dispose();
        _uiUpdateTimer?.Dispose();
        _signalingService.Dispose();
        _webRtcService.Dispose();
        _audioService.Dispose();
        // _memoryReader.Dispose(); // If GameDataReader implements IDisposable and needs cleanup
        
        // Clear tracking collections
        _pendingPeers.Clear();
        
        // SavePeerSettings(); // Settings are saved on change, so not strictly needed on dispose unless there's a specific case.
        Trace.TraceInformation("Disposing MainViewModel");
    }

    public void SetPushToTalkActive(bool isActive)
    {
        _audioService.SetPushToTalkActive(isActive);
    }

    // Method called by PeerViewModel Volume setter
    public void UpdatePeerVolumeFromViewModel(string peerId, float volume)
    {
        var peerVm = ConnectedPeers.FirstOrDefault(p => p.Id == peerId);
        if (peerVm != null && peerVm.CharacterName != PeerViewModel.DefaultCharacterName)
        {
            _audioService.SetPeerUiVolume(peerId, volume); // Assuming this updates live volume
            UpdateAndSavePeerSetting(peerVm.CharacterName, volume, peerVm.IsMuted);
        }
        else if (peerVm != null)
        {
            Debug.WriteLine($"UpdatePeerVolumeFromViewModel: Character name for {peerId} not yet known. Volume change not persisted yet.");
            _audioService.SetPeerUiVolume(peerId, volume); // Still apply live volume
        }
    }

    // New method called by PeerViewModel IsMuted setter
    public void UpdatePeerMuteStateFromViewModel(string peerId, bool isMuted)
    {
        var peerVm = ConnectedPeers.FirstOrDefault(p => p.Id == peerId);
        if (peerVm != null && peerVm.CharacterName != PeerViewModel.DefaultCharacterName)
        {
            _audioService.SetPeerMuteState(peerId, isMuted); // Tell audio service to mute/unmute
            UpdateAndSavePeerSetting(peerVm.CharacterName, peerVm.Volume, isMuted);
        }
         else if (peerVm != null)
        {
            Debug.WriteLine($"UpdatePeerMuteStateFromViewModel: Character name for {peerId} not yet known. Mute change not persisted yet.");
            _audioService.SetPeerMuteState(peerId, isMuted); // Still apply live mute
        }
    }
} 