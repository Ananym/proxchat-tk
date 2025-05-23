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
    private bool _useWavInput = false;

    // Fields to track last sent position for conditional updates
    private int? _lastSentMapId;
    private int _lastSentX; // Changed to int
    private int _lastSentY; // Changed to int
    private DateTime _lastSentTime = DateTime.MinValue;
    private readonly TimeSpan _forceSendInterval = TimeSpan.FromSeconds(5); // Changed from 10s to 5s
    private readonly TimeSpan _positionSendInterval = TimeSpan.FromMilliseconds(250); // Keep this for UI updates
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
    private readonly object _peersLock = new object(); // Added for synchronizing peer collection access

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
            _inputVolumeScale = Math.Clamp(value, 0.0f, 2.0f);
            _audioService.SetInputVolumeScale(_inputVolumeScale);
            OnPropertyChanged();
        }
    }

    public float VolumeScale
    {
        get => _volumeScale;
        set
        {
            _volumeScale = Math.Clamp(value, 0.0f, 2.0f);
            _audioService.SetOverallVolumeScale(_volumeScale);
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

    public bool UseWavInput
    {
        get => _useWavInput;
        set
        {
            if (_useWavInput != value)
            {
                _debugLog.LogMain($"audio input changed: wav={value}");
                _useWavInput = value;
                _audioService.UseAudioFileInput = value;
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
        
        // Get log file name from command line args
        string? logFileName = null;
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--log" || args[i] == "-l")
            {
                logFileName = args[i + 1];
                break;
            }
        }
        
        var debugLog = new DebugLogService(logFileName);
        _debugLog = debugLog;
        _audioService = new AudioService(config.AudioSettings.MaxDistance, config, debugLog);
        _signalingService = new SignalingService(config.WebSocketServer, debugLog);
        _webRtcService = new WebRtcService(_audioService, _signalingService, config.AudioSettings.MaxDistance, debugLog);
        
        // Subscribe to game data read events
        _memoryReader.GameDataRead += OnGameDataRead;

        StartCommand = new RelayCommand(() => {
            if (_isRunning)
            {
                Stop();
            }
            else
            {
                Start();
            }
        });
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
            // Tuple now returns (bool Success, int MapId, string MapName, ...)
            var (success, mapId, mapName, x, y, name) = _memoryReader.ReadPositionAndName();

            // Use the success flag from the game data
            bool currentReadSuccess = success;

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
            else if (!currentReadSuccess && _isMemoryReaderInitialized && !IsDebugModeEnabled)
            {
                // Lost connection after it was previously established
                // Only update _isMemoryReaderInitialized if not in debug mode
                _isMemoryReaderInitialized = false;
                StatusMessage = "Lost connection to game data provider. Waiting...";
                Debug.WriteLine("GameMemoryReader lost connection to MMF.");
            }

            // Check for timeout and auto-disconnect if conditions are met
            // Only check timeout if not in debug mode and we're running
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
            if (!IsDebugModeEnabled)
            {
                _isMemoryReaderInitialized = false; // Only update if not in debug mode
            }
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
            // Set debug mode initialization first
            if (IsDebugModeEnabled)
            {
                _isMemoryReaderInitialized = true;
            }

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
            
            // Only reset game data timeout tracking if not in debug mode
            if (!IsDebugModeEnabled)
            {
                _lastSuccessfulReadTime = DateTime.MinValue;
            }

            _positionSendTimer?.Dispose();
            // Update timer to tick every second FOR SENDING
            _positionSendTimer = new Timer(SendPositionUpdate, null, TimeSpan.Zero, _positionSendInterval); // Use the send interval

            // In debug mode, we don't need to wait for game data initialization
            if (IsDebugModeEnabled)
            {
                StatusMessage = "Running (Debug Mode)";
            }
            else
            {
                StatusMessage = "Running";
            }

            IsRunning = true;
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

    private async void OnGameDataRead(object? sender, (bool Success, int MapId, string MapName, int X, int Y, string CharacterName) data)
    {
        if (!IsRunning) return;

        try
        {
            var now = DateTime.UtcNow;
            bool mapChanged = data.MapId != _lastSentMapId;
            bool positionChanged = data.X != _lastSentX || data.Y != _lastSentY;
            bool forceSend = (now - _lastSentTime) >= _forceSendInterval;

            if ((mapChanged || positionChanged || forceSend) && _signalingService.IsConnected)
            {
                await _signalingService.UpdatePosition(data.MapId, data.X, data.Y);
                _lastSentMapId = data.MapId;
                _lastSentX = data.X;
                _lastSentY = data.Y;
                _lastSentTime = now;

                foreach (var peer in ConnectedPeers)
                {
                    _webRtcService.SendPosition(peer.Id, data.MapId, data.X, data.Y, data.CharacterName);
                    
                    if (peer.MapId == data.MapId)
                    {
                        var dx = data.X - peer.X;
                        var dy = data.Y - peer.Y;
                        var distance = MathF.Sqrt(dx * dx + dy * dy);
                        _debugLog.LogMain($"peer {peer.CharacterName}: dist={distance:F1} (me:{data.X},{data.Y} them:{peer.X},{peer.Y})");
                        _audioService.UpdatePeerDistance(peer.Id, distance);
                        peer.Distance = distance;
                    }
                    else
                    {
                        _audioService.UpdatePeerDistance(peer.Id, float.MaxValue);
                        peer.Distance = float.MaxValue;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error handling game data read: {ex.Message}");
        }
    }

    private async void SendPositionUpdate(object? state)
    {
        // This is now just a fallback/force update every 5 seconds
        if (!IsRunning) return;

        try
        {
            var now = DateTime.UtcNow;
            if ((now - _lastSentTime) >= _forceSendInterval && _signalingService.IsConnected)
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
                    var gameData = _memoryReader.ReadPositionAndName();
                    mapId = gameData.MapId;
                    x = gameData.X;
                    y = gameData.Y;
                    name = gameData.CharacterName;
                }

                await _signalingService.UpdatePosition(mapId, x, y);
                _lastSentMapId = mapId;
                _lastSentX = x;
                _lastSentY = y;
                _lastSentTime = now;

                // Only send position updates to peers in the UI (not pending peers)
                foreach (var peer in ConnectedPeers)
                {
                    _webRtcService.SendPosition(peer.Id, mapId, x, y, name);
                    
                    // Recalculate distance to this peer based on our new position
                    if (peer.MapId == mapId) // Only calculate if on same map
                    {
                        var dx = x - peer.X;
                        var dy = y - peer.Y;
                        var distance = MathF.Sqrt(dx * dx + dy * dy);
                        _debugLog.LogMain($"Recalculated distance to peer {peer.CharacterName}: {distance:F2} units");
                        _audioService.UpdatePeerDistance(peer.Id, distance);
                        peer.Distance = distance;
                    }
                    else
                    {
                        // Different map, set max distance
                        _audioService.UpdatePeerDistance(peer.Id, float.MaxValue);
                        peer.Distance = float.MaxValue;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in force position update: {ex.Message}");
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
    private async void HandleDataChannelOpened(object? sender, string peerId)
    {
        _debugLog.LogMain($"[UI_TIMING] HandleDataChannelOpened START for peer {peerId}.");
        // We will not add the peer to the ConnectedPeers collection here.
        // HandlePeerPosition will be responsible for adding the peer when the first position update is received.
        // This simplifies logic and avoids race conditions for adding.
        // However, we should send our current position to the peer whose data channel just opened,
        // so they become aware of us and can send their position back.
        _debugLog.LogMain($"[UI_TIMING] HandleDataChannelOpened for {peerId}: About to dispatch SendPositionUpdateForPeer.");
        await Task.Run(() => SendPositionUpdateForPeer(peerId)); // Run on a background thread
        _debugLog.LogMain($"[UI_TIMING] HandleDataChannelOpened for {peerId}: Dispatched SendPositionUpdateForPeer. Method returning.");
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
                var gameData = _memoryReader.ReadPositionAndName(); 
                mapId = gameData.MapId;
                x = gameData.X;
                y = gameData.Y;
                name = gameData.CharacterName;
            }

            _debugLog.LogMain($"[UI_TIMING] SendPositionUpdateForPeer START for peer {peerId}. My pos: Map={mapId}, X={x}, Y={y}, Name={name}.");
            _debugLog.LogMain($"[UI_TIMING] SendPositionUpdateForPeer for {peerId}: About to call _webRtcService.SendPosition.");
            _webRtcService.SendPosition(peerId, mapId, x, y, name);
            _debugLog.LogMain($"[UI_TIMING] SendPositionUpdateForPeer for {peerId}: Called _webRtcService.SendPosition. Method returning.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error sending position update to peer {peerId}: {ex.Message}");
        }
    }

    private void RecalculateDistanceAndVolume(PeerViewModel peerVm)
    {
        if (peerVm == null) return;

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
            // It's better to use the already updated CurrentMapId, CurrentX, CurrentY
            // to avoid re-reading from memory reader in this specific context.
            myMapId = this.CurrentMapId;
            myX = this.CurrentX;
            myY = this.CurrentY;
        }

        if (myMapId != peerVm.MapId)
        {
            _audioService.UpdatePeerDistance(peerVm.Id, float.MaxValue);
            peerVm.Distance = float.MaxValue;
            _debugLog.LogMain($"Peer {peerVm.CharacterName} ({peerVm.Id}) is on a different map. Setting distance to MaxValue.");
        }
        else
        {
            var dx = myX - peerVm.X;
            var dy = myY - peerVm.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);

            _debugLog.LogMain($"Recalculated distance for peer {peerVm.CharacterName} ({peerVm.Id}): {distance:F1} (me:{myX},{myY} them:{peerVm.X},{peerVm.Y})");
            _audioService.UpdatePeerDistance(peerVm.Id, distance);
            peerVm.Distance = distance;
        }
    }

    private void UpdatePeerViewModelProperties(PeerViewModel peerVm, (string PeerId, int MapId, int X, int Y, string CharacterName) positionData)
    {
        // Use a sensible default if CharacterName is empty or null, e.g., from PeerViewModel.PENDING_CHARACTER_NAME
        string newCharName = string.IsNullOrEmpty(positionData.CharacterName) ? PeerViewModel.DefaultCharacterName : positionData.CharacterName;

        // Use PeerId from positionData as it's the unique identifier from the source
        if (peerVm.CharacterName != newCharName && newCharName != PeerViewModel.DefaultCharacterName && !string.IsNullOrEmpty(newCharName))
        {
            _debugLog.LogMain($"Peer {positionData.PeerId}: updating name from '{peerVm.CharacterName}' to '{newCharName}'");
            peerVm.CharacterName = newCharName;
             // When name changes from default to a real name, apply persisted settings
            if (peerVm.CharacterName != PeerViewModel.DefaultCharacterName) // Check against the actual default name
            {
                ApplyPersistedSettings(peerVm);
            }
        }

        if (peerVm.MapId != positionData.MapId) peerVm.MapId = positionData.MapId;
        if (peerVm.X != positionData.X) peerVm.X = positionData.X;
        if (peerVm.Y != positionData.Y) peerVm.Y = positionData.Y;
    }

    private async void HandlePeerPosition(object? sender, (string PeerId, int MapId, int X, int Y, string CharacterName) positionData)
    {
        // This method can be called from background threads (e.g., via WebRtcService events)
        _debugLog.LogMain($"[UI_TIMING] HandlePeerPosition START for peer {positionData.PeerId}. Data: Map={positionData.MapId}, X={positionData.X}, Y={positionData.Y}, Name='{positionData.CharacterName}'.");

        PeerViewModel? existingPeerVm = null;
        bool needsToAdd = false;
        string peerIdentifier = positionData.PeerId; // Use PeerId from the incoming data consistently

        lock (_peersLock) // Lock for reading the collection
        {
            existingPeerVm = ConnectedPeers.FirstOrDefault(p => p.Id == peerIdentifier);
            if (existingPeerVm == null)
            {
                needsToAdd = true;
            }
        }

        if (needsToAdd)
        {
            _debugLog.LogMain($"Peer {peerIdentifier} (Name from data: {positionData.CharacterName}) not in UI collection. Preparing to create and add.");
            _debugLog.LogMain($"[UI_TIMING] HandlePeerPosition for {peerIdentifier}: Needs to add. About to Dispatcher.InvokeAsync.");
            
            // Create the new ViewModel outside the Dispatcher, but add it inside.
            var newPeerVmInstance = new PeerViewModel(); // Use default constructor
            newPeerVmInstance.Id = peerIdentifier;
            newPeerVmInstance.CharacterName = string.IsNullOrEmpty(positionData.CharacterName) ? PeerViewModel.DefaultCharacterName : positionData.CharacterName;
            newPeerVmInstance.MapId = positionData.MapId;
            newPeerVmInstance.X = positionData.X;
            newPeerVmInstance.Y = positionData.Y;
            newPeerVmInstance.Volume = 1.0f; // Default volume as per PeerViewModel field initializer

            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                lock (_peersLock) // Lock for the critical check-and-add section on the UI thread
                {
                    var peerAlreadyAdded = ConnectedPeers.FirstOrDefault(p => p.Id == newPeerVmInstance.Id);
                    if (peerAlreadyAdded == null)
                    {
                        _debugLog.LogMain($"[UI_TIMING] HandlePeerPosition for {newPeerVmInstance.Id} ({newPeerVmInstance.CharacterName}): ADDING to ConnectedPeers collection.");
                        ConnectedPeers.Add(newPeerVmInstance);
                        _debugLog.LogMain($"[UI_TIMING] HandlePeerPosition for {newPeerVmInstance.Id} ({newPeerVmInstance.CharacterName}): ADDED to ConnectedPeers collection.");
                        _debugLog.LogMain($"New peer {newPeerVmInstance.Id} ({newPeerVmInstance.CharacterName}) added to UI collection.");
                        // Apply persisted settings *after* adding and *after* CharacterName might be set
                        // The UpdatePeerViewModelProperties below will handle the initial name setting if needed.
                        UpdatePeerViewModelProperties(newPeerVmInstance, positionData); // Set initial properties
                        RecalculateDistanceAndVolume(newPeerVmInstance); 
                    }
                    else
                    {
                        _debugLog.LogMain($"[UI_TIMING] HandlePeerPosition for {peerAlreadyAdded.Id}: CONCURRENTLY ADDED. Updating its properties instead.");
                        _debugLog.LogMain($"Peer {peerAlreadyAdded.Id} was concurrently added. Updating its properties instead of adding new.");
                        UpdatePeerViewModelProperties(peerAlreadyAdded, positionData);
                        RecalculateDistanceAndVolume(peerAlreadyAdded);
                    }
                }
            });
        }
        else if (existingPeerVm != null) // Peer already exists, update its properties
        {
            _debugLog.LogMain($"Peer {existingPeerVm.Id} ({existingPeerVm.CharacterName}) exists in UI collection. Dispatching update.");
            _debugLog.LogMain($"[UI_TIMING] HandlePeerPosition for {existingPeerVm.Id} ({existingPeerVm.CharacterName}): Exists. About to Dispatcher.InvokeAsync to update properties.");
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                _debugLog.LogMain($"[UI_TIMING] HandlePeerPosition for {existingPeerVm.Id} ({existingPeerVm.CharacterName}): UPDATING existing peer in ConnectedPeers.");
                UpdatePeerViewModelProperties(existingPeerVm, positionData);
                RecalculateDistanceAndVolume(existingPeerVm);
            });
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
             // Send position update immediately after connecting
             _ = Task.Run(async () => {
                 try {
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
                         var gameData = _memoryReader.ReadPositionAndName();
                         mapId = gameData.MapId;
                         x = gameData.X;
                         y = gameData.Y;
                         name = gameData.CharacterName;
                     }

                     await _signalingService.UpdatePosition(mapId, x, y);
                     _lastSentMapId = mapId;
                     _lastSentX = x;
                     _lastSentY = y;
                     _lastSentTime = DateTime.UtcNow;

                     // Also send to any connected peers
                     foreach (var peer in ConnectedPeers)
                     {
                         _webRtcService.SendPosition(peer.Id, mapId, x, y, name);
                     }
                 }
                 catch (Exception ex)
                 {
                     Debug.WriteLine($"Error sending position update after reconnection: {ex.Message}");
                 }
             });
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
        _memoryReader.GameDataRead -= OnGameDataRead; // Unsubscribe from event
        _signalingService.Dispose();
        _webRtcService.Dispose();
        _audioService.Dispose();
        _memoryReader.Dispose(); // Now safe to dispose since we unsubscribed
        
        // Clear tracking collections
        _pendingPeers.Clear();
        
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

    private async void ForcePositionUpdate()
    {
        try
        {
            var now = DateTime.UtcNow;
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
                var gameData = _memoryReader.ReadPositionAndName();
                mapId = gameData.MapId;
                x = gameData.X;
                y = gameData.Y;
                name = gameData.CharacterName;
            }

            // Check if position has changed or if it's been 5 seconds
            bool positionChanged = mapId != _lastSentMapId || x != _lastSentX || y != _lastSentY;
            bool timeElapsed = (now - _lastSentTime).TotalSeconds >= 5;

            if (positionChanged || timeElapsed)
            {
                await _signalingService.UpdatePosition(mapId, x, y);
                _lastSentMapId = mapId;
                _lastSentX = x;
                _lastSentY = y;
                _lastSentTime = now;

                // Send position updates to all connected peers
                _webRtcService.SendPositionToAllPeers(mapId, x, y, name);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in force position update: {ex.Message}");
        }
    }
} 