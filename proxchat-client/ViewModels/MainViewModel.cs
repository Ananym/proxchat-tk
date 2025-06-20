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
using Microsoft.Win32; // For OpenFileDialog
// Ensure RelayCommand namespace is covered - assuming it's within ViewModels or a sub-namespace
// using ProxChatClient.Commands; // Example if it were in a ProxChatClient.Commands namespace

namespace ProxChatClient.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IGameDataReader _memoryReader;
    private readonly SignalingService _signalingService;
    private readonly WebRtcService _webRtcService;
    private readonly AudioService _audioService;
    private readonly GlobalHotkeyService _globalHotkeyService;
    private readonly Config _config;
    private string _statusMessage = "Initializing..."; // Start with initializing status
    private bool _isRunning;
    private float _audioLevel;
    private bool _isPushToTalk;
    private HotkeyDefinition _pushToTalkHotkey = new(Key.OemBackslash); // Changed from Key.F12 to backslash
    private bool _isEditingPushToTalk;
    private HotkeyDefinition _muteSelfHotkey = new(Key.M, ctrl: true); // Changed from Key.F11 to Ctrl+M
    private bool _isEditingMuteSelf; // Add editing state
    private bool _isPushToTalkActive; // Track if PTT is currently being pressed
    private float _volumeScale; // Will be initialized from config
    private float _inputVolumeScale; // Will be initialized from config
    private float _minBroadcastThreshold; // Will be initialized from config
    private Timer? _positionSendTimer; // Renamed from _positionTimer for clarity
    private bool _isMemoryReaderInitialized = false; // Flag for memory reader status

    // Debug Mode Fields - read-only after construction
    private readonly bool _isDebugModeEnabled;
    private DebugGameDataReader? _debugReader; // cast reference for debug mode event triggering
    private string _debugCharacterName = Guid.NewGuid().ToString().Substring(0, 8); // Default random string
    private int _debugX = 109; // Changed to int
    private int _debugY = 191; // Changed to int
    private int _debugMapId = 0; // Map 0 is perfectly valid - it's the first map
    private bool _useWavInput; // Debug-only, not persisted
    private string? _selectedAudioFile; // Path to selected audio file
    private string _audioFileDisplayName = "No file selected"; // Display name for UI

    // Fields to track last sent position for conditional updates
    private int? _lastSentMapId;
    private int _lastSentX; // Changed to int
    private int _lastSentY; // Changed to int
    private DateTime _lastSentTime = DateTime.MinValue;
    private readonly TimeSpan _forceSendInterval = TimeSpan.FromSeconds(5); // Changed from 10s to 5s

    // Source of truth for current game state - updated by OnGameDataRead
    private int _gameMapId = 0;
    private string _gameMapName = "N/A";
    private int _gameX = 0;
    private int _gameY = 0;
    private string _gameCharacterName = "Player";
    private int _gameId = 0;

    private readonly string _peerSettingsFilePath; // Added for dedicated peer settings file

    // Add this field with the other private fields around line 30
    private DateTime _lastSuccessfulReadTime = DateTime.MinValue;
    private readonly TimeSpan _gameDataTimeout = TimeSpan.FromSeconds(5);

    // Add this field with the other private fields around line 30
    private readonly DebugLogService _debugLog;
    private readonly object _peersLock = new object(); // Added for synchronizing peer collection access

    // Add a dictionary to track pending peers (not yet in UI)
    private readonly ConcurrentDictionary<string, bool> _pendingPeers = new(); 

    // add field to track last known character name for change detection
    private string? _lastKnownCharacterName = null;

    // track consecutive read failures for auto-disconnect
    private int _consecutiveReadFailures = 0;
    private const int MAX_CONSECUTIVE_FAILURES = 3;

    // add fields for logging state tracking
    private bool _shouldLogRead = false;
    
    // debug counters for OnGameDataRead
    private int _debugSuccessCount = 0;
    private int _debugFailureCount = 0;

    public ObservableCollection<PeerViewModel> ConnectedPeers { get; } = new();
    public ObservableCollection<string> InputDevices => _audioService.InputDevices;
    
    // Debug Mode Properties
    public bool IsDebugModeEnabled => _isDebugModeEnabled;

    public string DebugCharacterName
    {
        get => _debugCharacterName;
        set 
        { 
            if (_debugCharacterName != value)
            {
                string oldValue = _debugCharacterName;
                _debugCharacterName = value; 
                OnPropertyChanged(); 
                if (IsDebugModeEnabled) 
                {
                    OnPropertyChanged(nameof(CurrentCharacterName));
                    
                    // fire debug game data event when value changes
                    _debugReader?.FireDebugGameData(_debugMapId, "DebugMap", _debugX, _debugY, value, 0);
                    
                    // handle character name change in debug mode
                    if (!string.IsNullOrEmpty(value) && value != "Player" && IsRunning)
                    {
                        if (_lastKnownCharacterName != null && _lastKnownCharacterName != value)
                        {
                            _debugLog.LogMain($"Debug character name change detected: '{_lastKnownCharacterName}' -> '{value}'");
                            _ = Task.Run(async () => await HandleCharacterNameChange(value));
                        }
                        else if (_lastKnownCharacterName == null)
                        {
                            _lastKnownCharacterName = value;
                            _debugLog.LogMain($"Initial debug character name set: '{value}'");
                        }
                    }
                }
            }
        }
    }

    public int DebugX // Changed to int
    {
        get => _debugX;
        set 
        { 
            _debugX = value; 
            OnPropertyChanged(); 
            if (IsDebugModeEnabled) 
            {
                OnPropertyChanged(nameof(CurrentX));
                
                // fire debug game data event when value changes
                _debugReader?.FireDebugGameData(_debugMapId, "DebugMap", value, _debugY, _debugCharacterName, 0);
                
                // recalculate distances when debug position changes
                if (IsRunning) RecalculateAllPeerDistances();
            }
        }
    }

    public int DebugY // Changed to int
    {
        get => _debugY;
        set 
        { 
            _debugY = value; 
            OnPropertyChanged(); 
            if (IsDebugModeEnabled) 
            {
                OnPropertyChanged(nameof(CurrentY));
                
                // fire debug game data event when value changes
                _debugReader?.FireDebugGameData(_debugMapId, "DebugMap", _debugX, value, _debugCharacterName, 0);
                
                // recalculate distances when debug position changes
                if (IsRunning) RecalculateAllPeerDistances();
            }
        }
    }

    public int DebugMapId
    {
        get => _debugMapId;
        set 
        { 
            _debugMapId = value; 
            OnPropertyChanged(); 
            if (IsDebugModeEnabled) 
            {
                OnPropertyChanged(nameof(CurrentMapId));
                
                // fire debug game data event when value changes
                _debugReader?.FireDebugGameData(value, "DebugMap", _debugX, _debugY, _debugCharacterName, 0);
                
                // recalculate distances when debug map changes
                if (IsRunning) RecalculateAllPeerDistances();
            }
        }
    }

    // Debug Mode Commands
    public ICommand IncrementDebugXCommand { get; }
    public ICommand DecrementDebugXCommand { get; }
    public ICommand IncrementDebugYCommand { get; }
    public ICommand DecrementDebugYCommand { get; }
    public ICommand BrowseAudioFileCommand { get; }

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
            _inputVolumeScale = Math.Clamp(value, 0.0f, 5.0f);
            _audioService.SetInputVolumeScale(_inputVolumeScale);
            _config.AudioSettings.InputVolumeScale = _inputVolumeScale; // Save to config
            SaveConfig(); // Persist to file
            OnPropertyChanged();
        }
    }

    public float VolumeScale
    {
        get => _volumeScale;
        set
        {
            _volumeScale = Math.Clamp(value, 0.0f, 1.0f); // Keep 0-1 range
            _audioService.SetOverallVolumeScale(_volumeScale);
            _config.AudioSettings.VolumeScale = _volumeScale; // Save to config
            SaveConfig(); // Persist to file
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
                UpdateGlobalHotkeys(); // Update global hotkey service
                _config.AudioSettings.IsPushToTalk = _isPushToTalk; // Save to config
                SaveConfig(); // Persist to file
                OnPropertyChanged();
                if (!value) IsEditingPushToTalk = false; 
                ((RelayCommand)EditPushToTalkCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public HotkeyDefinition PushToTalkHotkey
    {
        get => _pushToTalkHotkey;
        set
        {
            if (_pushToTalkHotkey != value)
            {
                _pushToTalkHotkey = value;
                UpdateGlobalHotkeys(); // Update global hotkey service
                _config.AudioSettings.PushToTalkKey = value.ToString(); // Save to config
                SaveConfig(); // Persist to file
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
    public ICommand RefreshPeersCommand { get; }

    // UI properties that bind directly to source of truth
    public int CurrentMapId => IsDebugModeEnabled ? _debugMapId : _gameMapId;
    public string CurrentMapName => IsDebugModeEnabled ? "DebugMap" : _gameMapName;
    public string CurrentMapDisplay => IsRunning || _isMemoryReaderInitialized ? $"{CurrentMapName} ({CurrentMapId})" : "Waiting...";
    public int CurrentX => IsDebugModeEnabled ? _debugX : _gameX;
    public int CurrentY => IsDebugModeEnabled ? _debugY : _gameY;
    public string CurrentCharacterName => IsDebugModeEnabled ? _debugCharacterName : _gameCharacterName;

    public bool UseWavInput
    {
        get => _useWavInput;
        set
        {
            if (_useWavInput != value)
            {
                // If trying to enable file input, check if we have a valid file first
                if (value && (string.IsNullOrEmpty(_selectedAudioFile) || !File.Exists(_selectedAudioFile)))
                {
                    string errorMessage = string.IsNullOrEmpty(_selectedAudioFile) 
                        ? "Cannot enable audio file input: No audio file selected. Please select an audio file first."
                        : $"Cannot enable audio file input: Selected audio file not found ({Path.GetFileName(_selectedAudioFile)}). Please select a valid audio file.";
                    
                    StatusMessage = errorMessage;
                    _debugLog.LogMain($"Cannot enable file input: {errorMessage}");
                    
                    // Don't change the value, just refresh the UI to uncheck the box
                    OnPropertyChanged();
                    return;
                }
                
                _debugLog.LogMain($"audio input changed: wav={value}");
                try
                {
                    _audioService.UseAudioFileInput = value;
                    _useWavInput = value; // Only update the backing field if the AudioService accepts the change
                    _debugLog.LogMain($"Successfully changed audio input mode to file input: {value}");
                }
                catch (Exception ex)
                {
                    _debugLog.LogMain($"Failed to change audio input mode: {ex.Message}");
                    _debugLog.LogMain($"Exception details: {ex}");
                    StatusMessage = $"Failed to change audio input mode: {ex.Message}";
                    
                    // Don't update _useWavInput since the operation failed
                    OnPropertyChanged(); // Still notify UI to refresh the checkbox state
                    return;
                }
                
                // UseWavInput is debug-only, not persisted to config
                OnPropertyChanged();
            }
        }
    }

    public string? SelectedAudioFile
    {
        get => _selectedAudioFile;
        set
        {
            if (_selectedAudioFile != value)
            {
                _selectedAudioFile = value;
                
                try
                {
                    _audioService.SetCustomAudioFile(value);
                    _debugLog.LogMain($"Selected audio file: {value ?? "none"}");
                    
                    // Update display name
                    if (string.IsNullOrEmpty(value))
                    {
                        AudioFileDisplayName = "No file selected";
                    }
                    else
                    {
                        AudioFileDisplayName = Path.GetFileName(value);
                    }
                    
                    // Clear any previous error status if we successfully set the file
                    if (UseWavInput && !string.IsNullOrEmpty(value) && File.Exists(value))
                    {
                        // If file input is enabled and we selected a valid file,
                        // update status to show the audio is ready
                        StatusMessage = _audioService.GetAudioInputStatus();
                    }
                }
                catch (Exception ex)
                {
                    _debugLog.LogMain($"Error setting custom audio file: {ex.Message}");
                    AudioFileDisplayName = "Error loading file";
                    if (UseWavInput)
                    {
                        StatusMessage = $"Audio file error: {ex.Message}";
                    }
                }
                
                OnPropertyChanged();
            }
        }
    }

    public string AudioFileDisplayName
    {
        get => _audioFileDisplayName;
        set
        {
            if (_audioFileDisplayName != value)
            {
                _audioFileDisplayName = value;
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
                _config.AudioSettings.MinBroadcastThreshold = _minBroadcastThreshold; // Save to config
                SaveConfig(); // Persist to file
                OnPropertyChanged();
            }
        }
    }

    public HotkeyDefinition MuteSelfHotkey
    {
        get => _muteSelfHotkey;
        set
        {
            if (_muteSelfHotkey != value)
            {
                _muteSelfHotkey = value;
                UpdateGlobalHotkeys(); // Update global hotkey service
                _config.AudioSettings.MuteSelfKey = value.ToString(); // Save to config
                SaveConfig(); // Persist to file
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

    public bool IsPushToTalkActive
    {
        get => _isPushToTalkActive;
        private set
        {
            if (_isPushToTalkActive != value)
            {
                _isPushToTalkActive = value;
                OnPropertyChanged();
            }
        }
    }

    public MainViewModel(Config config, bool isDebugModeEnabled = false)
    {
        _config = config;
        _isDebugModeEnabled = isDebugModeEnabled;
        
        // Initialize audio settings from config
        _volumeScale = _config.AudioSettings.VolumeScale > 0 ? _config.AudioSettings.VolumeScale : 0.5f; // Default to 0.5f if not set
        _inputVolumeScale = _config.AudioSettings.InputVolumeScale;
        _minBroadcastThreshold = _config.AudioSettings.MinBroadcastThreshold;
        _isPushToTalk = _config.AudioSettings.IsPushToTalk;
        
        // Load hotkeys from config with fallbacks
        _pushToTalkHotkey = HotkeyDefinition.FromStringWithDefault(_config.AudioSettings.PushToTalkKey, Key.OemBackslash);
        _muteSelfHotkey = GlobalHotkeyService.StringToHotkeyWithDefault(_config.AudioSettings.MuteSelfKey, new HotkeyDefinition(Key.M, ctrl: true));
        
        // Notify UI that hotkeys have been loaded
        OnPropertyChanged(nameof(PushToTalkHotkey));
        OnPropertyChanged(nameof(MuteSelfHotkey));
        
        // Ensure UI controls are updated after construction completes
        Task.Run(async () =>
        {
            await Task.Delay(100); // Small delay to ensure UI is ready
            App.Current.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(PushToTalkHotkey));
                OnPropertyChanged(nameof(MuteSelfHotkey));
            });
        });
        
        // UseWavInput is debug-only, not persisted
        _useWavInput = false;
        
        // Define path for peer settings, e.g., in user's app data or alongside main config
        _peerSettingsFilePath = Path.Combine(AppContext.BaseDirectory, "PeerSettings.json");
        LoadPeerSettings(); // Load settings on startup

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
        _debugLog.LogMain("MainViewModel constructor started");

        try
        {
            // choose appropriate game data reader implementation based on debug mode
            _debugLog.LogMain($"DEBUG FLAG CHECK: _isDebugModeEnabled = {_isDebugModeEnabled}");
            if (_isDebugModeEnabled)
            {
                _debugLog.LogMain("DEBUG MODE: Creating DebugGameDataReader - no pipe connection will be made");
                _debugReader = new DebugGameDataReader(_debugLog);
                _memoryReader = _debugReader;
                _debugLog.LogMain("DEBUG MODE: DebugGameDataReader successfully created and assigned");
            }
            else
            {
                _debugLog.LogMain("NORMAL MODE: Creating NamedPipeGameDataReader");
                _memoryReader = new NamedPipeGameDataReader(_config.GameDataIpcChannel, _debugLog);
                _debugLog.LogMain("NORMAL MODE: NamedPipeGameDataReader successfully created");
            }
            
            // Use hardcoded max distance to match server's disconnect range
            const float HARDCODED_MAX_DISTANCE = 25.0f; // Match server's disconnection range for consistency
            
            _debugLog.LogMain("Initializing AudioService");
            _audioService = new AudioService(HARDCODED_MAX_DISTANCE, config, debugLog);
            
            _debugLog.LogMain("Initializing SignalingService");
            _signalingService = new SignalingService(config.WebSocketServer, debugLog);
            
            _debugLog.LogMain("Initializing WebRtcService");
            _webRtcService = new WebRtcService(_audioService, _signalingService, HARDCODED_MAX_DISTANCE, debugLog);
            
            _debugLog.LogMain("Initializing GlobalHotkeyService");
            _globalHotkeyService = new GlobalHotkeyService(debugLog);
            
            _debugLog.LogMain("Services initialized successfully");
        }
        catch (Exception ex)
        {
            _debugLog.LogMain($"ERROR during service initialization: {ex.Message}");
            _debugLog.LogMain($"ERROR stack trace: {ex.StackTrace}");
            throw;
        }

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
        }, () => {
            // enable start button if:
            // 1. currently running (to allow stop), OR
            // 2. not running AND (debug mode enabled OR memory reader is working)
            return _isRunning || (!_isRunning && (IsDebugModeEnabled || _isMemoryReaderInitialized));
        });
        StopCommand = new RelayCommand(Stop, () => _isRunning);
        ToggleMuteCommand = new RelayCommand<string>(TogglePeerMute);
        EditPushToTalkCommand = new RelayCommand(() => { IsEditingPushToTalk = !IsEditingPushToTalk; }, () => IsPushToTalk);
        RefreshDevicesCommand = new RelayCommand(_audioService.RefreshInputDevices);
        ToggleSelfMuteCommand = new RelayCommand(ToggleSelfMute);
        EditMuteSelfCommand = new RelayCommand(() => { IsEditingMuteSelf = !IsEditingMuteSelf; });
        RefreshPeersCommand = new RelayCommand(RefreshPeers);

        // Initialize Debug Commands
        IncrementDebugXCommand = new RelayCommand(() => DebugX++);
        DecrementDebugXCommand = new RelayCommand(() => DebugX--);
        IncrementDebugYCommand = new RelayCommand(() => DebugY++);
        DecrementDebugYCommand = new RelayCommand(() => DebugY--);
        BrowseAudioFileCommand = new RelayCommand(BrowseAudioFile);

        _signalingService.NearbyClientsReceived += HandleNearbyClients;
        _signalingService.ConnectionStatusChanged += HandleSignalingConnectionStatus;
        _signalingService.SignalingErrorReceived += HandleSignalingError;
        _webRtcService.PositionReceived += HandlePeerPosition;
        _webRtcService.DataChannelOpened += HandleDataChannelOpened;
        _audioService.AudioLevelChanged += (_, level) => AudioLevel = level;
        _audioService.PeerTransmissionChanged += HandlePeerTransmissionChanged;
        _audioService.RefreshedDevices += (s, e) => { 
            OnPropertyChanged(nameof(InputDevices));
            var currentSelection = SelectedInputDevice;
            SelectedInputDevice = (currentSelection != null && _audioService.InputDevices.Contains(currentSelection)) 
                                ? currentSelection 
                                : _audioService.InputDevices.FirstOrDefault();
        };

        // Subscribe to global hotkey events
        _globalHotkeyService.PushToTalkStateChanged += HandlePushToTalkStateChanged;
        _globalHotkeyService.MuteToggleRequested += HandleMuteToggleRequested;
        
        // Initialize hotkey settings
        UpdateGlobalHotkeys();
        
        // Start global hotkeys
        _globalHotkeyService.StartHook();

        RefreshDevicesCommand.Execute(null);

        _positionSendTimer = new Timer(SendPositionUpdate, null, Timeout.Infinite, Timeout.Infinite);
        
        // initialize memory reader status
        InitializeMemoryReader();
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

    private void SaveConfig()
    {
        try
        {
            string json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText("config.json", json);
            Debug.WriteLine("Saved config to config.json");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving config: {ex.Message}");
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
        
        // ViewModel is the single source of truth - ensure AudioService reflects ViewModel state
        _audioService.SyncPeerVolumeFromViewModel(peerVm.Id, peerVm.Volume);
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
        if (_isDebugModeEnabled)
        {
            // in debug mode, no pipe connection is attempted - consider it always initialized
            _isMemoryReaderInitialized = true;
            StatusMessage = "Debug Mode Active. Game data is being overridden.";
            _debugLog.LogMain("Debug mode enabled - memory reader marked as initialized, no pipe connection");
        }
        else
        {
            StatusMessage = "Attempting to establish connection with game data provider...";
            // memory reader initialization is now handled by the OnGameDataRead event
            // when the first successful read occurs, _isMemoryReaderInitialized will be set to true
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
        // Validate audio input configuration before starting
        bool isAudioReady = _audioService.IsAudioInputReady();
        string audioStatus = _audioService.GetAudioInputStatus();
        _debugLog.LogMain($"[DEBUG] Audio input ready: {isAudioReady}, status: {audioStatus}");
        
        if (!isAudioReady)
        {
            StatusMessage = audioStatus;
            _debugLog.LogMain($"[DEBUG] Cannot start - audio input not ready: {audioStatus}");
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
                _debugLog.LogMain("[DEBUG] Audio capture started successfully");
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
            // Timer for force position updates every 5 seconds (fallback when no game data changes)
            _positionSendTimer = new Timer(SendPositionUpdate, null, TimeSpan.Zero, _forceSendInterval); // Use the send interval

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
        
        // clear pending peers and reset character name tracking
        _pendingPeers.Clear();
        _lastKnownCharacterName = null;
        _consecutiveReadFailures = 0; // reset failure counter when stopping

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

    private async void OnGameDataRead(object? sender, (bool Success, int MapId, string MapName, int X, int Y, string CharacterName, int GameId) data)
    {
        var startTime = DateTime.UtcNow;
        
        // log first few calls to confirm the event handler is being triggered
        var totalCallCount = _debugSuccessCount + _debugFailureCount + 1;
        if (totalCallCount <= 5)
        {
            _debugLog.LogMain($"OnGameDataRead call #{totalCallCount}: Success={data.Success}, IsRunning={IsRunning}, _isMemoryReaderInitialized={_isMemoryReaderInitialized}");
        }
        
        // debug: log first few successful reads to confirm data flow
        if (data.Success)
        {
            _debugSuccessCount++;
            if (_debugSuccessCount <= 3)
            {
                _debugLog.LogMain($"OnGameDataRead SUCCESS #{_debugSuccessCount}: Map={data.MapId}('{data.MapName}'), Pos=({data.X},{data.Y}), Char='{data.CharacterName}'");
            }
        }
        else
        {
            _debugFailureCount++;
            if (_debugFailureCount <= 3)
            {
                _debugLog.LogMain($"OnGameDataRead FAILURE #{_debugFailureCount}");
            }
        }
        
        // only log game data reads when critical state changes occur
        if (!data.Success && !_shouldLogRead)
        {
            _debugLog.LogMain($"OnGameDataRead: First failure detected");
            _shouldLogRead = true;
        }
        else if (data.Success && _shouldLogRead)
        {
            _debugLog.LogMain($"OnGameDataRead: Success recovered - Map={data.MapId}, X={data.X}, Y={data.Y}, Name='{data.CharacterName}'");
            _shouldLogRead = false;
        }
        
        try
        {
            // handle memory reader initialization status (this should happen regardless of IsRunning)
            if (data.Success)
            {
                _lastSuccessfulReadTime = DateTime.UtcNow;
                _consecutiveReadFailures = 0;
                
                if (!_isMemoryReaderInitialized)
                {
                    var processingTime = DateTime.UtcNow - startTime;
                    _isMemoryReaderInitialized = true;
                    StatusMessage = "Game data connection established. Ready.";
                    _debugLog.LogMain($"Memory reader initialized - status set to Ready. IsRunning={IsRunning}, ProcessingTime={processingTime.TotalMilliseconds:F1}ms");
                    Debug.WriteLine("GameMemoryReader successfully connected to MMF.");
                    
                    // reset debug counters on successful reconnection
                    _debugSuccessCount = 0;
                    _debugFailureCount = 0;
                    _shouldLogRead = false;
                    _debugLog.LogMain($"Reset debug counters on successful reconnection");
                    
                    ((RelayCommand)StartCommand).RaiseCanExecuteChanged();
                }
                else
                {
                    // log successful reads periodically even when already initialized
                    if (_debugSuccessCount % 50 == 1) // log every 50th success = 2%
                    {
                        _debugLog.LogMain($"OnGameDataRead: Success #{_debugSuccessCount} while already initialized. IsRunning={IsRunning}");
                    }
                }
            }
            else if (!data.Success && _isMemoryReaderInitialized && !IsDebugModeEnabled)
            {
                _consecutiveReadFailures++;
                _debugLog.LogMain($"OnGameDataRead: Failure #{_consecutiveReadFailures}/{MAX_CONSECUTIVE_FAILURES}. IsRunning={IsRunning}");
                
                if (IsRunning && _consecutiveReadFailures >= MAX_CONSECUTIVE_FAILURES)
                {
                    _debugLog.LogMain($"Auto-disconnecting after {_consecutiveReadFailures} consecutive read failures");
                    _ = Task.Run(async () => await StopAsync());
                    return;
                }
                
                if (_consecutiveReadFailures >= MAX_CONSECUTIVE_FAILURES)
                {
                    _isMemoryReaderInitialized = false;
                    StatusMessage = "Lost connection to game data provider. Waiting...";
                    _debugLog.LogMain($"Memory reader marked as uninitialized after {_consecutiveReadFailures} failures");
                    Debug.WriteLine("GameMemoryReader lost connection to MMF.");
                    ((RelayCommand)StartCommand).RaiseCanExecuteChanged();
                    
                    if (IsRunning)
                    {
                        _debugLog.LogMain($"Clearing connected peers due to data provider failure while running");
                        ClearAllConnectedPeers();
                        _lastKnownCharacterName = null;
                    }
                }
            }

            // update source of truth fields and trigger UI updates (this should happen regardless of IsRunning)
            bool positionChanged = false;
            if (data.Success)
            {
                if (_gameMapId != data.MapId)
                {
                    _gameMapId = data.MapId;
                    positionChanged = true;
                    OnPropertyChanged(nameof(CurrentMapId));
                    OnPropertyChanged(nameof(CurrentMapDisplay));
                }
                if (_gameMapName != data.MapName)
                {
                    _gameMapName = data.MapName;
                    OnPropertyChanged(nameof(CurrentMapName));
                    OnPropertyChanged(nameof(CurrentMapDisplay));
                }
                if (_gameX != data.X)
                {
                    _gameX = data.X;
                    positionChanged = true;
                    OnPropertyChanged(nameof(CurrentX));
                }
                if (_gameY != data.Y)
                {
                    _gameY = data.Y;
                    positionChanged = true;
                    OnPropertyChanged(nameof(CurrentY));
                }
                if (_gameCharacterName != data.CharacterName)
                {
                    _gameCharacterName = data.CharacterName;
                    OnPropertyChanged(nameof(CurrentCharacterName));
                }
                if (_gameId != data.GameId)
                {
                    _gameId = data.GameId;
                }
            }

            // early return if not running - position sending and peer logic only happens when running
            if (!IsRunning) return;

            // check for character name changes in game data read
            if (data.Success && !string.IsNullOrEmpty(data.CharacterName) && data.CharacterName != "Player")
            {
                // check if character name has changed to a new non-null/non-empty value
                if (_lastKnownCharacterName != null && _lastKnownCharacterName != data.CharacterName)
                {
                    _debugLog.LogMain($"Character name change detected in game data: '{_lastKnownCharacterName}' -> '{data.CharacterName}'");
                    // handle character name change asynchronously
                    _ = Task.Run(async () => await HandleCharacterNameChange(data.CharacterName));
                    return; // exit early, reconnection will handle position updates
                }
                else if (_lastKnownCharacterName == null)
                {
                    // first time we've seen a character name
                    _lastKnownCharacterName = data.CharacterName;
                    _debugLog.LogMain($"Initial character name detected in game data: '{data.CharacterName}'");
                }
            }
            
            var now = DateTime.UtcNow;
            bool mapChanged = data.MapId != _lastSentMapId;
            bool positionChangedForSending = data.X != _lastSentX || data.Y != _lastSentY;
            bool forceSend = (now - _lastSentTime) >= _forceSendInterval;

            if ((mapChanged || positionChangedForSending || forceSend) && _signalingService.IsConnected)
            {
                // use debug values if debug mode is enabled, otherwise use game data
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
                    mapId = _gameMapId;
                    x = _gameX;
                    y = _gameY;
                    name = _gameCharacterName;
                }

                await _signalingService.UpdatePosition(mapId, x, y, _config.Channel, _gameId);
                _lastSentMapId = mapId;
                _lastSentX = x;
                _lastSentY = y;
                _lastSentTime = now;

                foreach (var peer in ConnectedPeers)
                {
                    _webRtcService.SendPosition(peer.Id, mapId, x, y, name);
                }
            }
            
            // recalculate distances for all peers when local position changes
            if (positionChanged)
            {
                RecalculateAllPeerDistances();
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
                    mapId = _gameMapId;
                    x = _gameX;
                    y = _gameY;
                    name = _gameCharacterName;
                }

                await _signalingService.UpdatePosition(mapId, x, y, _config.Channel, _gameId);
                _lastSentMapId = mapId;
                _lastSentX = x;
                _lastSentY = y;
                _lastSentTime = now;

                // Only send position updates to peers in the UI (not pending peers)
                foreach (var peer in ConnectedPeers)
                {
                    _webRtcService.SendPosition(peer.Id, mapId, x, y, name);
                }
                
                // recalculate distances for all peers when local position changes
                RecalculateAllPeerDistances();
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

            // only log when there are actual changes
            if (toAddIds.Any() || toRemoveIds.Any())
            {
                Debug.WriteLine($"Nearby update: Add={string.Join(",", toAddIds)}, Remove={string.Join(",", toRemoveIds)}");
            }

            // check if our own client ID is in the nearby list
            if (nearbyClients.Contains(myClientId))
            {
                _debugLog.LogMain($"[BUG] Our own client ID {myClientId} is in the nearby peers list!");
                
                // remove our own ID from the list to prevent self-connection
                nearbyClients = nearbyClients.Where(id => id != myClientId).ToList();
                _debugLog.LogMain($"[FIX] Removed own client ID from nearby list. Remaining peers: {string.Join(", ", nearbyClients)}");
            }

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

            // CHANGE: Process new connections with temporal spreading to reduce system hitching
            if (toAddIds.Any())
            {
                _debugLog.LogMain($"Processing {toAddIds.Count} new peer connections with temporal spreading");
                
                // Process connections with staggered delays to reduce system load
                for (int i = 0; i < toAddIds.Count; i++)
                {
                    var peerId = toAddIds[i];
                    
                    // Don't add to UI yet - add to pending peers and initiate connection
                    if (!_pendingPeers.ContainsKey(peerId))
                    {
                        bool amInitiator = string.CompareOrdinal(myClientId, peerId) > 0;
                        Debug.WriteLine($"Adding new pending peer {peerId}, Initiator: {amInitiator}");
                        
                        // only log map info when adding peers if it's interesting
                        var currentMapId = IsDebugModeEnabled ? _debugMapId : _gameMapId;
                        _debugLog.LogMain($"Adding peer {peerId} while on MapId={currentMapId}. Initiator: {amInitiator}");
                        
                        _pendingPeers[peerId] = true;
                        
                        // Create WebRTC connection with delay to spread load over time
                        var connectionDelay = i * 250; // 250ms delay between each connection attempt
                        _ = Task.Run(async () =>
                        {
                            if (connectionDelay > 0)
                            {
                                await Task.Delay(connectionDelay);
                            }
                            
                            // verify peer is still needed before creating connection
                            if (_pendingPeers.ContainsKey(peerId) && IsRunning)
                            {
                                await _webRtcService.CreatePeerConnection(peerId, amInitiator);
                            }
                        });
                        
                        // Set up a timeout to clean up failed connections
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(15000 + connectionDelay); // Account for initial delay
                            
                            // Check if peer is still pending (not connected)
                            if (_pendingPeers.ContainsKey(peerId))
                            {
                                var connectedPeer = ConnectedPeers.FirstOrDefault(p => p.Id == peerId);
                                if (connectedPeer == null)
                                {
                                    _debugLog.LogMain($"WebRTC connection timeout for peer {peerId}, cleaning up and allowing retry");
                                    
                                    // Clean up the failed connection attempt
                                    _pendingPeers.TryRemove(peerId, out _);
                                    _webRtcService.RemovePeerConnection(peerId);
                                    
                                    // Request peer refresh to trigger reintroduction
                                    try
                                    {
                                        await _signalingService.RequestPeerRefresh();
                                    }
                                    catch (Exception ex)
                                    {
                                        _debugLog.LogMain($"Error requesting peer refresh after timeout: {ex.Message}");
                                    }
                                }
                            }
                        });
                    }
                }
            }
        });
    }

    // handler for the new DataChannelOpened event
    private async void HandleDataChannelOpened(object? sender, string peerId)
    {
        // send our current position to the peer whose data channel just opened,
        // so they become aware of us and can send their position back
        await Task.Run(() => SendPositionUpdateForPeer(peerId));
    }
    
    // send position update to a specific peer
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
                mapId = _gameMapId;
                x = _gameX;
                y = _gameY;
                name = _gameCharacterName;
            }

            _webRtcService.SendPosition(peerId, mapId, x, y, name);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error sending position update to peer {peerId}: {ex.Message}");
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

        bool positionChanged = false;
        if (peerVm.MapId != positionData.MapId) 
        {
            peerVm.MapId = positionData.MapId;
            positionChanged = true;
        }
        if (peerVm.X != positionData.X) 
        {
            peerVm.X = positionData.X;
            positionChanged = true;
        }
        if (peerVm.Y != positionData.Y) 
        {
            peerVm.Y = positionData.Y;
            positionChanged = true;
        }
        
        if (positionChanged)
        {
            // only log map changes (removed regular position change logging)
            if (peerVm.MapId != positionData.MapId)
            {
                _debugLog.LogMain($"Peer {positionData.PeerId} map change: Map={positionData.MapId}");
            }
            
            // recalculate distance when peer position changes
            RecalculatePeerDistance(peerVm);
        }
    }

    // centralized method to calculate and update peer distance
    private void RecalculatePeerDistance(PeerViewModel peerVm)
    {
        // get current local position from the single source of truth
        int localMapId;
        int localX;
        int localY;
        
        if (IsDebugModeEnabled)
        {
            localMapId = _debugMapId;
            localX = _debugX;
            localY = _debugY;
        }
        else
        {
            localMapId = _gameMapId;
            localX = _gameX;
            localY = _gameY;
        }
        
        // calculate distance
        if (localMapId == peerVm.MapId)
        {
            var dx = localX - peerVm.X;
            var dy = localY - peerVm.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            
            // only log distance calculations for debugging if needed (removed regular logging)
            
            // update audio service with new position and distance
            _audioService.UpdatePeerPosition(peerVm.Id, distance, localX, localY, peerVm.X, peerVm.Y);
            
            // update UI
            peerVm.Distance = distance;
        }
        else
        {
            // different map, set max distance but display as "Different Map"
            _debugLog.LogMain($"Peer {peerVm.CharacterName} on different map: MyMapId={localMapId}, PeerMapId={peerVm.MapId}");
            _audioService.UpdatePeerDistance(peerVm.Id, float.MaxValue);
            
            // use a special value that the UI can recognize and display as "Different Map"
            peerVm.Distance = -1.0f; // Special value to indicate different map
        }
    }

    // recalculate distances for all peers (called when local position changes)
    private void RecalculateAllPeerDistances()
    {
        foreach (var peer in ConnectedPeers)
        {
            RecalculatePeerDistance(peer);
        }
    }

    private async void HandlePeerPosition(object? sender, (string PeerId, int MapId, int X, int Y, string CharacterName) positionData)
    {
        // check if we're receiving position data from our own client ID
        var myClientId = _signalingService.ClientId;
        if (positionData.PeerId == myClientId)
        {
            _debugLog.LogMain($"[BUG] Received position data from our own client ID {myClientId}!");
            return; // don't process our own position data
        }

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
            // create the new ViewModel outside the Dispatcher, but add it inside
            var newPeerVmInstance = new PeerViewModel();
            newPeerVmInstance.Id = peerIdentifier;
            newPeerVmInstance.CharacterName = string.IsNullOrEmpty(positionData.CharacterName) ? PeerViewModel.DefaultCharacterName : positionData.CharacterName;
            newPeerVmInstance.MapId = positionData.MapId;
            newPeerVmInstance.X = positionData.X;
            newPeerVmInstance.Y = positionData.Y;
            newPeerVmInstance.Volume = 0.5f; // default volume to 0.5f to leave room for boosting specific peers
            
            // viewModel is the single source of truth for peer volume - ensure AudioService gets it immediately
            _audioService.SyncPeerVolumeFromViewModel(newPeerVmInstance.Id, newPeerVmInstance.Volume);

            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                lock (_peersLock) // lock for the critical check-and-add section on the UI thread
                {
                    var peerAlreadyAdded = ConnectedPeers.FirstOrDefault(p => p.Id == newPeerVmInstance.Id);
                    if (peerAlreadyAdded == null)
                    {
                        ConnectedPeers.Add(newPeerVmInstance);
                        _debugLog.LogMain($"New peer {newPeerVmInstance.Id} ({newPeerVmInstance.CharacterName}) added to UI collection");
                        UpdatePeerViewModelProperties(newPeerVmInstance, positionData); // set initial properties
                        RecalculatePeerDistance(newPeerVmInstance); // calculate initial distance for new peer
                        
                        // Initialize transmission state for new peer in case audio packets arrived before position data
                        bool currentTransmissionState = _audioService.GetPeerTransmissionState(newPeerVmInstance.Id);
                        newPeerVmInstance.IsTransmitting = currentTransmissionState;
                    }
                    else
                    {
                        _debugLog.LogMain($"Peer {peerAlreadyAdded.Id} was concurrently added. Updating its properties instead of adding new");
                        UpdatePeerViewModelProperties(peerAlreadyAdded, positionData);
                    }
                }
            });
        }
        else if (existingPeerVm != null) // peer already exists, update its properties
        {
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                UpdatePeerViewModelProperties(existingPeerVm, positionData);
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
                         mapId = _gameMapId;
                         x = _gameX;
                         y = _gameY;
                         name = _gameCharacterName;
                     }

                     await _signalingService.UpdatePosition(mapId, x, y, _config.Channel, _gameId);
                     _lastSentMapId = mapId;
                     _lastSentX = x;
                     _lastSentY = y;
                     _lastSentTime = DateTime.UtcNow;

                     // Also send to any connected peers
                     foreach (var peer in ConnectedPeers)
                     {
                         _webRtcService.SendPosition(peer.Id, mapId, x, y, name);
                     }
                     
                     // Request fresh peer list after reconnection
                     await Task.Delay(500); // Brief delay to ensure position update is processed
                     await _signalingService.RequestPeerRefresh();
                     _debugLog.LogMain("Requested peer refresh after signaling reconnection");
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
        _memoryReader.GameDataRead -= OnGameDataRead; // Unsubscribe from event
        _signalingService.Dispose();
        _webRtcService.Dispose();
        _audioService.PeerTransmissionChanged -= HandlePeerTransmissionChanged; // Unsubscribe from transmission events
        _audioService.Dispose();
        _globalHotkeyService?.Dispose(); // Dispose global hotkey service
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
                mapId = _gameMapId;
                x = _gameX;
                y = _gameY;
                name = _gameCharacterName;
            }

            // Check if position has changed or if it's been 5 seconds
            bool positionChanged = mapId != _lastSentMapId || x != _lastSentX || y != _lastSentY;
            bool timeElapsed = (now - _lastSentTime).TotalSeconds >= 5;

            if (positionChanged || timeElapsed)
            {
                await _signalingService.UpdatePosition(mapId, x, y, _config.Channel, _gameId);
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

    private void HandlePeerTransmissionChanged(object? sender, (string PeerId, bool IsTransmitting) transmissionData)
    {
        var (peerId, isTransmitting) = transmissionData;
        
        // Find the peer in the UI and update their transmission status
        App.Current.Dispatcher.Invoke(() =>
        {
            var peerVm = ConnectedPeers.FirstOrDefault(p => p.Id == peerId);
            if (peerVm != null)
            {
                peerVm.IsTransmitting = isTransmitting;
                // only log first transmission start for each peer, not every change
                if (isTransmitting && !peerVm.HasLoggedFirstTransmission)
                {
                    _debugLog.LogMain($"Peer {peerVm.CharacterName} ({peerId}) first transmission detected");
                    peerVm.HasLoggedFirstTransmission = true;
                }
            }
        });
    }

    private void HandlePushToTalkStateChanged(object? sender, bool isActive)
    {
        _audioService.SetPushToTalkActive(isActive);
        IsPushToTalkActive = isActive; // Update UI indicator
    }

    private void HandleMuteToggleRequested(object? sender, EventArgs e)
    {
        ToggleSelfMute();
    }

    private void UpdateGlobalHotkeys()
    {
        _globalHotkeyService.UpdateHotkeys(_pushToTalkHotkey, _muteSelfHotkey, _isPushToTalk);
    }

    private void RefreshPeers()
    {
        if (!IsRunning || !_signalingService.IsConnected)
        {
            _debugLog.LogMain("Cannot refresh peers: not running or not connected to signaling server");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                _debugLog.LogMain("Manual peer refresh requested");
                await _signalingService.RequestPeerRefresh();
            }
            catch (Exception ex)
            {
                _debugLog.LogMain($"Error requesting manual peer refresh: {ex.Message}");
            }
        });
    }

    // add method to clear all connected peers
    private void ClearAllConnectedPeers()
    {
        _debugLog.LogMain("Clearing all connected peers");
        
        App.Current.Dispatcher.Invoke(() =>
        {
            lock (_peersLock)
            {
                ConnectedPeers.Clear();
            }
        });
        
        // clear pending peers
        _pendingPeers.Clear();
        
        // clean up webrtc and audio resources
        _webRtcService.CloseAllConnections();
        
        // note: audio service peer cleanup is handled by webrtc service
    }

    // add method to handle character name changes
    private async Task HandleCharacterNameChange(string newCharacterName)
    {
        if (!IsRunning) return;
        
        _debugLog.LogMain($"Character name changed from '{_lastKnownCharacterName}' to '{newCharacterName}' - regenerating client ID for anonymity");
        
        try
        {
            // clear all connected peers first
            ClearAllConnectedPeers();
            
            // regenerate client id and reconnect to signaling server
            await _signalingService.RegenerateClientIdAndReconnect();
            
            // update last known character name
            _lastKnownCharacterName = newCharacterName;
            
            _debugLog.LogMain($"Successfully reconnected with new client ID after character name change");
        }
        catch (Exception ex)
        {
            _debugLog.LogMain($"Error handling character name change: {ex.Message}");
            StatusMessage = $"Error reconnecting after character change: {ex.Message}";
        }
    }

    private void BrowseAudioFile()
    {
        try
        {
            var openFileDialog = new OpenFileDialog
            {
                Title = "Select Audio File",
                Filter = "Audio Files|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac|" +
                        "WAV Files|*.wav|" +
                        "MP3 Files|*.mp3|" +
                        "M4A Files|*.m4a|" +
                        "AAC Files|*.aac|" +
                        "WMA Files|*.wma|" +
                        "FLAC Files|*.flac|" +
                        "All Files|*.*",
                FilterIndex = 1,
                CheckFileExists = true,
                CheckPathExists = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                string selectedFile = openFileDialog.FileName;
                
                // Validate the selected file
                if (_audioService.IsValidAudioFile(selectedFile))
                {
                    SelectedAudioFile = selectedFile;
                    _debugLog.LogMain($"Audio file selected: {selectedFile}");
                }
                else
                {
                    MessageBox.Show(
                        "The selected file is not a supported audio format or cannot be accessed.",
                        "Invalid Audio File",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    _debugLog.LogMain($"Invalid audio file selected: {selectedFile}");
                }
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogMain($"Error browsing for audio file: {ex.Message}");
            MessageBox.Show(
                $"Error selecting audio file: {ex.Message}",
                "File Selection Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
} 