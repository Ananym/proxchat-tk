using System;
using System.Collections.Concurrent;
using System.Linq; // Added for First()
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ProxChatClient.Models.Signaling;
using ProxChatClient.Services;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;
using System.IO; // For file logging
using System.Text.Json;
using System.Diagnostics; // Added for performance monitoring

namespace ProxChatClient.Services;

public class WebRtcService : IDisposable
{
    private readonly ConcurrentDictionary<string, PeerConnectionState> _peerConnections = new();
    private readonly SignalingService _signalingService;
    private readonly AudioService _audioService;
    private readonly float _maxDistance;
    private readonly DebugLogService _debugLog;
    private bool _isInitialized;

    private static readonly Random _random = new Random(); // For probabilistic logging
    private const double LOG_PROBABILITY = 0.02; // Enable some logging to debug core issue

    private static readonly List<RTCIceServer> _stunServers = new List<RTCIceServer>
    {
        // Primary - Most reliable
        new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" },
        new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
        
        // Backup Google servers
        new RTCIceServer { urls = "stun:stun1.l.google.com:19302" },
        new RTCIceServer { urls = "stun:stun2.l.google.com:19302" },
        
        // Additional reliable alternatives
        new RTCIceServer { urls = "stun:freestun.net:3478" },
        new RTCIceServer { urls = "stun:stunserver2024.stunprotocol.org:3478" }
    };
    private VirtualAudioSource? _virtualAudioSource;
    private MediaStreamTrack? _audioTrack;

    // max 2 concurrent connections
    private readonly SemaphoreSlim _connectionSemaphore = new SemaphoreSlim(2, 2);
    
    // heartbeat mechanism for fast disconnect detection
    private readonly Timer _heartbeatTimer;
    private readonly ConcurrentDictionary<string, DateTime> _lastHeartbeatReceived = new();
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromMilliseconds(500); // send heartbeat every 500ms
    private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(1); // disconnect after 1 second without heartbeat
    
    // system resource monitoring for performance analysis
    private static void LogSystemResources(string peerId, string phase, DebugLogService debugLog)
    {
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            var workingSet = currentProcess.WorkingSet64 / (1024 * 1024); // MB
            var privateMemory = currentProcess.PrivateMemorySize64 / (1024 * 1024); // MB
            var cpuTime = currentProcess.TotalProcessorTime.TotalMilliseconds;
            var threadCount = currentProcess.Threads.Count;
            
            var gcMemory = GC.GetTotalMemory(false) / (1024 * 1024); // MB
            var gcGen0 = GC.CollectionCount(0);
            var gcGen1 = GC.CollectionCount(1);
            var gcGen2 = GC.CollectionCount(2);
            
            debugLog.LogWebRtc($"[SYSRES] {phase} for {peerId}: WorkingSet={workingSet}MB, PrivateMem={privateMemory}MB, " +
                              $"GCMem={gcMemory}MB, Threads={threadCount}, GC(0/1/2)={gcGen0}/{gcGen1}/{gcGen2}");
        }
        catch (Exception ex)
        {
            debugLog.LogWebRtc($"[SYSRES] Error monitoring resources for {peerId}: {ex.Message}");
        }
    }
    
    public event EventHandler<(string PeerId, int MapId, int X, int Y, string CharacterName)>? PositionReceived;
    public event EventHandler<string>? DataChannelOpened;

    private class PositionData
    {
        public int MapId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public string CharacterName { get; set; } = "";
    }

    private class HeartbeatMessage
    {
        public string Type { get; set; } = "heartbeat";
        public long Timestamp { get; set; }
    }

    private class DataChannelMessage
    {
        public string Type { get; set; } = "";
        public int MapId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public string CharacterName { get; set; } = "";
        public long Timestamp { get; set; }
    }

    public WebRtcService(AudioService audioService, SignalingService signalingService, float maxDistance, DebugLogService debugLog)
    {
        _audioService = audioService;
        _signalingService = signalingService;
        _maxDistance = maxDistance;
        _debugLog = debugLog;
        _isInitialized = false;

        InitializeAudioSystem();

        // VirtualAudioSource will connect directly to AudioService events
        // no need for manual subscription here

        EnsureInitialized();
        
        _heartbeatTimer = new Timer(SendHeartbeats, null, _heartbeatInterval, _heartbeatInterval);
    }

    private void EnsureInitialized()
    {
        if (_isInitialized) return;

        try
        {
            _signalingService.OfferReceived += OnOfferReceived;
            _signalingService.AnswerReceived += OnAnswerReceived;
            _signalingService.IceCandidateReceived += OnIceCandidateReceived;

            _isInitialized = true;
        }
        catch (Exception ex)
        {
            _debugLog.LogWebRtc($"Error during WebRTC initialization: {ex}");
            throw;
        }
    }

    private void InitializeAudioSystem()
    {
        _debugLog.LogWebRtc("InitializeAudioSystem started");
        
        try
        {
            // Clean up existing resources
            if (_virtualAudioSource != null)
            {
                _debugLog.LogWebRtc("Cleaning up existing VirtualAudioSource");
                _virtualAudioSource.CloseAudio().Wait();
                _virtualAudioSource = null;
            }

            // Create VirtualAudioSource as our audio aggregator
            _debugLog.LogWebRtc("Creating VirtualAudioSource");
            _virtualAudioSource = new VirtualAudioSource(_audioService, _debugLog);
            _debugLog.LogWebRtc("Created VirtualAudioSource as audio aggregator");
            
            // Create media track with the VirtualAudioSource
            _debugLog.LogWebRtc("Creating MediaStreamTrack");
            _audioTrack = new MediaStreamTrack(_virtualAudioSource.GetAudioSourceFormats(), MediaStreamStatusEnum.SendOnly);
            _debugLog.LogWebRtc($"Created audio track with VirtualAudioSource");

            // Start the audio source
            _debugLog.LogWebRtc("Starting VirtualAudioSource");
            _virtualAudioSource.StartAudio().Wait();
            _debugLog.LogWebRtc("VirtualAudioSource started and connected to track");
        }
        catch (Exception ex)
        {
            _debugLog.LogWebRtc($"ERROR initializing audio system with VirtualAudioSource: {ex.Message}");
            _debugLog.LogWebRtc($"ERROR stack trace: {ex.StackTrace}");
            
            // Clean up any partially initialized resources
            if (_virtualAudioSource != null)
            {
                try
                {
                    _virtualAudioSource.CloseAudio().Wait();
                }
                catch { }
                _virtualAudioSource = null;
            }
            _audioTrack = null;
            
            throw;
        }
    }

    public async Task CreatePeerConnection(string peerId, bool isInitiator)
    {
        var connectionStartTime = DateTime.UtcNow;
        _debugLog.LogWebRtc($"[PERF] Starting connection creation for {peerId} at {connectionStartTime:HH:mm:ss.fff}");
        LogSystemResources(peerId, "CONNECTION_START", _debugLog);
        
        EnsureInitialized();
        
        // CRITICAL SAFETY: Never create connection to ourselves
        if (peerId == _signalingService.ClientId)
        {
            _debugLog.LogWebRtc($"[CRITICAL BUG PREVENTION] Attempted to create peer connection to own client ID {peerId}! Blocking this.");
            return;
        }
        
        if (_peerConnections.ContainsKey(peerId)) return;
        if (_audioTrack == null)
        {
            _debugLog.LogWebRtc($"Cannot create peer connection for {peerId}: audio track is null");
            return;
        }

        // CHANGE: Use semaphore to limit concurrent connection attempts
        var semaphoreWaitStart = DateTime.UtcNow;
        await _connectionSemaphore.WaitAsync();
        var semaphoreWaitTime = DateTime.UtcNow - semaphoreWaitStart;
        _debugLog.LogWebRtc($"[PERF] Semaphore wait for {peerId}: {semaphoreWaitTime.TotalMilliseconds:F1}ms");
        LogSystemResources(peerId, "SEMAPHORE_ACQUIRED", _debugLog);
        
        try
        {
            _debugLog.LogWebRtc($"Starting WebRTC connection creation for {peerId} (concurrent limit: {_connectionSemaphore.CurrentCount + 1}/{_connectionSemaphore.CurrentCount + 1})");
            
            // Run intensive WebRTC initialization on thread pool to prevent UI blocking
            await Task.Run(async () =>
            {
                RTCPeerConnection? pc = null;
                try
                {
                    var peerConnectionCreateStart = DateTime.UtcNow;
                    pc = new RTCPeerConnection(new RTCConfiguration { iceServers = _stunServers });
                    var peerConnectionCreateTime = DateTime.UtcNow - peerConnectionCreateStart;
                    _debugLog.LogWebRtc($"[PERF] RTCPeerConnection creation for {peerId}: {peerConnectionCreateTime.TotalMilliseconds:F1}ms");
                    LogSystemResources(peerId, "RTCPC_CREATED", _debugLog);

                    var state = new PeerConnectionState { PeerConnection = pc };
                    if (!_peerConnections.TryAdd(peerId, state))
                    {
                        pc.Close("Failed to add to dictionary");
                        return;
                    }

                    try
                    {
                    // --- Media Tracks ---
                    var addTrackStart = DateTime.UtcNow;
                    pc.addTrack(_audioTrack);
                    var addTrackTime = DateTime.UtcNow - addTrackStart;
                    _debugLog.LogWebRtc($"[PERF] addTrack for {peerId}: {addTrackTime.TotalMilliseconds:F1}ms");
                    LogSystemResources(peerId, "TRACK_ADDED", _debugLog);

                    // Connect VirtualAudioSource to this peer connection
                    if (_virtualAudioSource != null)
                    {
                        var audioSourceConnectStart = DateTime.UtcNow;
                        _virtualAudioSource.OnAudioSourceEncodedSample += pc.SendAudio;
                        var audioSourceConnectTime = DateTime.UtcNow - audioSourceConnectStart;
                        _debugLog.LogWebRtc($"[PERF] VirtualAudioSource connection for {peerId}: {audioSourceConnectTime.TotalMilliseconds:F1}ms");
                    }

                    // Set up format negotiation for the audio source
                    pc.OnAudioFormatsNegotiated += (formats) =>
                    {
                        if (_virtualAudioSource != null && formats.Any())
                        {
                            var formatSetStart = DateTime.UtcNow;
                            _virtualAudioSource.SetAudioSourceFormat(formats.First());
                            var formatSetTime = DateTime.UtcNow - formatSetStart;
                            _debugLog.LogWebRtc($"[PERF] Audio format negotiation for {peerId}: {formatSetTime.TotalMilliseconds:F1}ms, Format: {formats.First().Codec}");
                        }
                    };

                    // Hook up the RTP packet received event to the audio end point
                    pc.OnRtpPacketReceived += (ep, mediaType, rtpPacket) => 
                    {
                        if (mediaType == SDPMediaTypesEnum.audio)
                        {
                            try 
                            {
                                string? foundPeerId = null;

                                // Find our application-level peerId by matching the pc instance that fired the event
                                foreach (var entry in _peerConnections)
                                {
                                    if (object.ReferenceEquals(entry.Value.PeerConnection, pc))
                                    {
                                        foundPeerId = entry.Key;
                                        break;
                                    }
                                }

                                if (foundPeerId != null)
                                {
                                    uint incomingSsrc = rtpPacket.Header.SyncSource; // Get SSRC for logging

                                    bool hasAudio = false;
                                    if (rtpPacket.Payload.Length > 0)
                                    {
                                        // For Opus, check if packet is not empty (Opus silence is typically very small packets or DTX)
                                        hasAudio = rtpPacket.Payload.Length > 2; // Opus silence/DTX packets are usually 1-2 bytes
                                    }

                                    // Removed verbose RTP logging to focus on core issues

                                    _audioService.PlayAudio(foundPeerId, rtpPacket.Payload, rtpPacket.Payload.Length);
                                }
                                else
                                {
                                    _debugLog.LogWebRtc($"[ERROR] Received audio RTP packet (SSRC: {rtpPacket.Header.SyncSource}) on an RTCPeerConnection instance that was not found in the _peerConnections dictionary.");
                                }
                            }
                            catch (Exception ex)
                            {
                                _debugLog.LogWebRtc($"Error processing received audio RTP packet: {ex.Message}");
                            }
                        }
                    };

                    // --- Connection State ---
                    pc.onconnectionstatechange += (connState) =>
                    {
                        var stateChangeTime = DateTime.UtcNow;
                        _debugLog.LogWebRtc($"[PERF] Connection state change for {peerId} at {stateChangeTime:HH:mm:ss.fff}: {connState}");
                        if (connState == RTCPeerConnectionState.connected)
                        {
                            var totalConnectionTime = stateChangeTime - connectionStartTime;
                            _debugLog.LogWebRtc($"[PERF] TOTAL CONNECTION TIME for {peerId}: {totalConnectionTime.TotalMilliseconds:F1}ms");
                            LogSystemResources(peerId, "CONNECTION_ESTABLISHED", _debugLog);
                            _debugLog.LogWebRtc($"[AUDIO_FLOW] Peer {peerId} connected - will now receive audio packets if any are being generated");
                        }
                        else if (connState == RTCPeerConnectionState.failed)
                        {
                            var totalFailureTime = stateChangeTime - connectionStartTime;
                            _debugLog.LogWebRtc($"[PERF] Connection FAILED for {peerId} after {totalFailureTime.TotalMilliseconds:F1}ms, requesting peer refresh");
                            LogSystemResources(peerId, "CONNECTION_FAILED", _debugLog);
                            RemovePeerConnection(peerId);
                            // Request fresh peer list in case this peer is still nearby but connection failed
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(1000); // Brief delay before requesting refresh
                                    await _signalingService.RequestPeerRefresh();
                                }
                                catch (Exception ex)
                                {
                                    _debugLog.LogWebRtc($"Error requesting peer refresh after connection failure: {ex.Message}");
                                }
                            });
                        }
                        else if (connState == RTCPeerConnectionState.closed)
                        {
                            RemovePeerConnection(peerId);
                        }
                    };

                    // --- ICE Candidates ---
                    var iceCandidateSetupStart = DateTime.UtcNow;
                    pc.onicecandidate += (candidate) =>
                    {
                        if (candidate != null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var candidateJson = candidate.toJSON();
                                    _debugLog.LogWebRtc($"Sending ICE candidate to {peerId}: {candidateJson}");
                                    await _signalingService.SendIceCandidateAsync(peerId, candidateJson).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _debugLog.LogWebRtc($"Error sending ICE candidate for {peerId}: {ex.Message}");
                                }
                            });
                        }
                    };
                    var iceCandidateSetupTime = DateTime.UtcNow - iceCandidateSetupStart;
                    _debugLog.LogWebRtc($"[PERF] ICE candidate setup for {peerId}: {iceCandidateSetupTime.TotalMilliseconds:F1}ms");

                    // Data Channel: Only the designated initiator explicitly creates the data channel.
                    // The other peer will receive it via the ondatachannel event.
                    if (isInitiator) // isInitiator from MainViewModel string comparison
                    {
                        var dataChannelCreateStart = DateTime.UtcNow;
                        var dataChannel = await pc.createDataChannel("position").ConfigureAwait(false);
                        var dataChannelCreateTime = DateTime.UtcNow - dataChannelCreateStart;
                        _debugLog.LogWebRtc($"[PERF] Data channel creation for {peerId}: {dataChannelCreateTime.TotalMilliseconds:F1}ms");
                        LogSystemResources(peerId, "DATACHANNEL_CREATED", _debugLog);
                        
                        if (dataChannel == null)
                        {
                            _debugLog.LogWebRtc($"Failed to create data channel for {peerId} as initiator");
                            RemovePeerConnection(peerId);
                            return;
                        }
                        ConfigureDataChannel(peerId, dataChannel); // Configure it right away for the creator
                        _debugLog.LogWebRtc($"VM Initiator (for peer {peerId}) created data channel 'position'.");
                    }
                    // Note: Non-initiators do NOT create a data channel - they receive it via ondatachannel event below
                    
                    // All peers listen for incoming data channels.
                    pc.ondatachannel += (dc) =>
                    {
                        _debugLog.LogWebRtc($"[WEBRTC_DC_TIMING] Received data channel '{dc.label}' from peer {peerId}. ReadyState: {dc.readyState}. Configuring...");
                        ConfigureDataChannel(peerId, dc); // Configure it when received
                    };

                    // Offer/Answer Flow
                    if (isInitiator) // This peer (determined by MainViewModel) will make the offer
                    {
                        try
                        {
                            _debugLog.LogWebRtc($"[INITIATOR LOG] About to call pc.createOffer() for peer {peerId}.");
                            
                            // CHANGE: Run createOffer on task thread to avoid blocking + measure time
                            var createOfferStart = DateTime.UtcNow;
                            LogSystemResources(peerId, "PRE_CREATE_OFFER", _debugLog);
                            RTCSessionDescriptionInit offer = await Task.Run(() => pc.createOffer());
                            var createOfferTime = DateTime.UtcNow - createOfferStart;
                            _debugLog.LogWebRtc($"[PERF] createOffer() for {peerId}: {createOfferTime.TotalMilliseconds:F1}ms");
                            LogSystemResources(peerId, "POST_CREATE_OFFER", _debugLog);

                            _debugLog.LogWebRtc($"Created offer for peer {peerId} with SDP: {offer.sdp}");

                            // Offerer sets its local description with the offer.
                            _debugLog.LogWebRtc($"[INITIATOR LOG] About to call pc.setLocalDescription(offer) for peer {peerId}.");
                            var setLocalDescStart = DateTime.UtcNow;
                            await pc.setLocalDescription(offer); // Async, Task (void)
                            var setLocalDescTime = DateTime.UtcNow - setLocalDescStart;
                            _debugLog.LogWebRtc($"[PERF] setLocalDescription(offer) for {peerId}: {setLocalDescTime.TotalMilliseconds:F1}ms");
                            LogSystemResources(peerId, "POST_SET_LOCAL_DESC", _debugLog);

                            _debugLog.LogWebRtc($"Set local description with offer for peer {peerId}.");

                            _debugLog.LogWebRtc($"[INITIATOR LOG] About to call _signalingService.SendOfferAsync for peer {peerId}.");
                            var sendOfferStart = DateTime.UtcNow;
                            await _signalingService.SendOfferAsync(peerId, offer.toJSON()).ConfigureAwait(false);
                            var sendOfferTime = DateTime.UtcNow - sendOfferStart;
                            _debugLog.LogWebRtc($"[PERF] SendOfferAsync for {peerId}: {sendOfferTime.TotalMilliseconds:F1}ms");
                            
                            _debugLog.LogWebRtc($"Offer sent to {peerId}. Local description is set. Waiting for answer.");
                        }
                        catch (Exception ex)
                        {
                            _debugLog.LogWebRtc($"Error in offer creation/sending for peer {peerId}: {ex.Message}");
                            RemovePeerConnection(peerId);
                        }
                    }
                    // The OnOfferReceived handler will process offers for the non-VM-initiator.
                    // The OnAnswerReceived handler will process answers for the VM-initiator.
                    
                    var setupCompleteTime = DateTime.UtcNow - connectionStartTime;
                    _debugLog.LogWebRtc($"[PERF] Connection setup phase complete for {peerId}: {setupCompleteTime.TotalMilliseconds:F1}ms");
                    LogSystemResources(peerId, "SETUP_COMPLETE", _debugLog);
                    }
                    catch (Exception ex)
                    {
                        _debugLog.LogWebRtc($"Error in CreatePeerConnection for {peerId}: {ex.Message}");
                        RemovePeerConnection(peerId);
                    }
                }
                catch (Exception ex)
                {
                    _debugLog.LogWebRtc($"CRITICAL: Unhandled exception during WebRTC connection creation for {peerId}: {ex.Message}");
                    _debugLog.LogWebRtc($"Stack trace: {ex.StackTrace}");
                    
                    // Clean up the peer connection if it was created
                    if (pc != null)
                    {
                        try
                        {
                            pc.Close("Exception during initialization");
                        }
                        catch (Exception cleanupEx)
                        {
                            _debugLog.LogWebRtc($"Error during cleanup for {peerId}: {cleanupEx.Message}");
                        }
                    }
                    
                    // Remove from dictionary if it was added
                    RemovePeerConnection(peerId);
                }
            });
        }
        catch (Exception ex)
        {
            _debugLog.LogWebRtc($"CRITICAL: Outer exception during WebRTC connection creation for {peerId}: {ex.Message}");
            _debugLog.LogWebRtc($"Stack trace: {ex.StackTrace}");
            
            // Ensure cleanup happens even if Task.Run fails
            RemovePeerConnection(peerId);
        }
        finally
        {
            _connectionSemaphore.Release();
            var totalSemaphoreTime = DateTime.UtcNow - connectionStartTime;
            _debugLog.LogWebRtc($"[PERF] Released semaphore for {peerId}, total time in semaphore: {totalSemaphoreTime.TotalMilliseconds:F1}ms");
        }
    }

    private void ConfigureDataChannel(string peerId, RTCDataChannel dc)
    {
        if (peerId != null && _peerConnections.TryGetValue(peerId, out var state))
        {
            state.DataChannel = dc; 
            _debugLog.LogWebRtc($"[WEBRTC_DC_TIMING] ConfigureDataChannel for peer {peerId} with DC {dc.label}. Current readyState: {dc.readyState}");
            
            dc.onmessage += (channel, type, data) =>
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (data == null) return;
                        string message = Encoding.UTF8.GetString(data);
                        
                        // Try to parse as generic data channel message first
                        var dcMessage = System.Text.Json.JsonSerializer.Deserialize<DataChannelMessage>(message);
                        if (dcMessage == null) return;

                        if (dcMessage.Type == "heartbeat")
                        {
                            // Update heartbeat tracking
                            _lastHeartbeatReceived[peerId] = DateTime.UtcNow;
                            // Don't log heartbeats - too verbose
                        }
                        else
                        {
                            // Assume it's a position message (legacy format without "type" field)
                            // TEMP LOG: Received raw message
                            _debugLog.LogWebRtc($"[TEMP] DC Message From {peerId}: Raw='{message}'");
                            
                            // For backwards compatibility, also try the old PositionData format
                            var posData = JsonConvert.DeserializeObject<PositionData>(message);
                            if (posData != null)
                            {
                                // TEMP LOG: Received deserialized position data
                                _debugLog.LogWebRtc($"[TEMP] DC Position From {peerId}: MapId={posData.MapId}, X={posData.X}, Y={posData.Y}, CharName='{posData.CharacterName}'");

                                var eventData = (peerId, posData.MapId, posData.X, posData.Y, posData.CharacterName);
                                PositionReceived?.Invoke(this, eventData);
                                _debugLog.LogWebRtc($"Received position update from {peerId}: MapId={posData.MapId}, X={posData.X}, Y={posData.Y}");
                                
                                // Update heartbeat tracking when we receive position data too
                                _lastHeartbeatReceived[peerId] = DateTime.UtcNow;
                            }
                            else if (dcMessage.MapId != 0 || !string.IsNullOrEmpty(dcMessage.CharacterName))
                            {
                                // New format position message
                                var eventData = (peerId, dcMessage.MapId, dcMessage.X, dcMessage.Y, dcMessage.CharacterName);
                                PositionReceived?.Invoke(this, eventData);
                                _debugLog.LogWebRtc($"Received position update from {peerId}: MapId={dcMessage.MapId}, X={dcMessage.X}, Y={dcMessage.Y}");
                                
                                // Update heartbeat tracking when we receive position data too
                                _lastHeartbeatReceived[peerId] = DateTime.UtcNow;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _debugLog.LogWebRtc($"Error parsing data channel message from {peerId}: {ex.Message}");
                    }
                });
            };
            
            _debugLog.LogWebRtc($"[WEBRTC_DC_TIMING] ConfigureDataChannel for peer {peerId} with DC {dc.label}. Setting up onopen handler.");
            dc.onopen += () => {
                _debugLog.LogWebRtc($"[WEBRTC_DC_TIMING] Data channel ONOPEN fired for peer {peerId}, DC {dc.label}. ReadyState: {dc.readyState}");
                DataChannelOpened?.Invoke(this, peerId);
            };
            
            // If data channel is already open, fire the event immediately
            if (dc.readyState == RTCDataChannelState.open)
            {
                _debugLog.LogWebRtc($"[WEBRTC_DC_TIMING] Data channel for peer {peerId} is already open, firing event immediately");
                DataChannelOpened?.Invoke(this, peerId);
            }
        }
    }

    private void OnOfferReceived(object? sender, OfferPayload payload)
    {
        if (payload.SenderId == null || payload.Offer == null) return;

        _ = Task.Run(async () => // Ensure async Task
        {
            try
            {
            var offerProcessingStart = DateTime.UtcNow;
            _debugLog.LogWebRtc($"[PERF] Starting offer processing from {payload.SenderId} at {offerProcessingStart:HH:mm:ss.fff}");
            
            var peerId = payload.SenderId;
            RTCPeerConnection pc;
            PeerConnectionState? state; // Declare here

            // Check if peer connection exists, if not create it
            if (!_peerConnections.TryGetValue(peerId, out state) || state == null)
            {
                var peerConnectionCreateStart = DateTime.UtcNow;
                // This client is the non-initiator, so create peer connection without data channel
                // The data channel will come via ondatachannel event when we set remote description
                await CreatePeerConnection(peerId, isInitiator: false).ConfigureAwait(false);
                var peerConnectionCreateTime = DateTime.UtcNow - peerConnectionCreateStart;
                _debugLog.LogWebRtc($"[PERF] Created peer connection for offer from {peerId}: {peerConnectionCreateTime.TotalMilliseconds:F1}ms");
                
                if (!_peerConnections.TryGetValue(peerId, out state) || state == null)
                {
                    _debugLog.LogWebRtc($"[OnOfferReceived] Failed to create or retrieve peer connection for {peerId} after offer.");
                    return;
                }
                pc = state.PeerConnection;
            }
            else
            {
                pc = state.PeerConnection;
            }

            try
            {
                var offerDeserializeStart = DateTime.UtcNow;
                var offerSdp = JsonConvert.DeserializeObject<RTCSessionDescriptionInit>(payload.Offer); 
                var offerDeserializeTime = DateTime.UtcNow - offerDeserializeStart;
                _debugLog.LogWebRtc($"[PERF] Offer JSON deserialization for {peerId}: {offerDeserializeTime.TotalMilliseconds:F1}ms");
                
                if (offerSdp == null) return;
                
                _debugLog.LogWebRtc($"Received offer from {peerId} with SDP: {offerSdp.sdp}");
                
                // Setting remote description will trigger ondatachannel event if data channel is in SDP
                var setRemoteDescStart = DateTime.UtcNow;
                var result = pc.setRemoteDescription(offerSdp); // Synchronous, returns SetDescriptionResultEnum
                var setRemoteDescTime = DateTime.UtcNow - setRemoteDescStart;
                _debugLog.LogWebRtc($"[PERF] setRemoteDescription(offer) for {peerId}: {setRemoteDescTime.TotalMilliseconds:F1}ms");
                
                if (result != SetDescriptionResultEnum.OK)
                {
                    _debugLog.LogWebRtc($"Failed to set remote description from offer for {peerId}: {result}");
                    RemovePeerConnection(peerId);
                    return;
                }
                else
                {
                    _debugLog.LogWebRtc($"Set remote description from offer for {peerId} successfully.");
                }

                var createAnswerStart = DateTime.UtcNow;
                var answer = pc.createAnswer(); // Synchronous
                var createAnswerTime = DateTime.UtcNow - createAnswerStart;
                _debugLog.LogWebRtc($"[PERF] createAnswer() for {peerId}: {createAnswerTime.TotalMilliseconds:F1}ms");
                _debugLog.LogWebRtc($"Created answer for {peerId} with SDP: {answer.sdp}");

                var setLocalDescAnswerStart = DateTime.UtcNow;
                await pc.setLocalDescription(answer); // Async, Task (void)
                var setLocalDescAnswerTime = DateTime.UtcNow - setLocalDescAnswerStart;
                _debugLog.LogWebRtc($"[PERF] setLocalDescription(answer) for {peerId}: {setLocalDescAnswerTime.TotalMilliseconds:F1}ms");
                _debugLog.LogWebRtc($"Set local description with answer for {peerId}.");

                var sendAnswerStart = DateTime.UtcNow;
                await _signalingService.SendAnswerAsync(peerId, answer.toJSON()).ConfigureAwait(false);
                var sendAnswerTime = DateTime.UtcNow - sendAnswerStart;
                _debugLog.LogWebRtc($"[PERF] SendAnswerAsync for {peerId}: {sendAnswerTime.TotalMilliseconds:F1}ms");
                
                var totalOfferProcessingTime = DateTime.UtcNow - offerProcessingStart;
                _debugLog.LogWebRtc($"[PERF] TOTAL OFFER PROCESSING TIME for {peerId}: {totalOfferProcessingTime.TotalMilliseconds:F1}ms");
            }
            catch (Exception ex)
            {
                _debugLog.LogWebRtc($"Error processing offer from {peerId}: {ex.Message}");
                RemovePeerConnection(peerId);
            }
            }
            catch (Exception ex)
            {
                _debugLog.LogWebRtc($"CRITICAL: Unhandled exception in OnOfferReceived for {payload.SenderId}: {ex.Message}");
                _debugLog.LogWebRtc($"Stack trace: {ex.StackTrace}");
                
                // Ensure cleanup happens
                if (payload.SenderId != null)
                {
                    RemovePeerConnection(payload.SenderId);
                }
            }
        });
    }

    private void OnAnswerReceived(object? sender, AnswerPayload payload)
    {
        if (payload.SenderId == null || payload.Answer == null) return;

        _ = Task.Run(() => // remove async since no await is used
        {
            try
            {
            var answerProcessingStart = DateTime.UtcNow;
            _debugLog.LogWebRtc($"[PERF] Starting answer processing from {payload.SenderId} at {answerProcessingStart:HH:mm:ss.fff}");
            
            var peerId = payload.SenderId;
            if (_peerConnections.TryGetValue(peerId, out var state) && state != null)
            {
                try
                {
                    var answerDeserializeStart = DateTime.UtcNow;
                    var answerSdp = JsonConvert.DeserializeObject<RTCSessionDescriptionInit>(payload.Answer); 
                    var answerDeserializeTime = DateTime.UtcNow - answerDeserializeStart;
                    _debugLog.LogWebRtc($"[PERF] Answer JSON deserialization for {peerId}: {answerDeserializeTime.TotalMilliseconds:F1}ms");
                    
                    if (answerSdp == null) return;
                    
                    _debugLog.LogWebRtc($"Received answer from {peerId} with SDP: {answerSdp.sdp}");
                    
                    var setRemoteDescAnswerStart = DateTime.UtcNow;
                    var result = state.PeerConnection.setRemoteDescription(answerSdp); // Synchronous, returns SetDescriptionResultEnum
                    var setRemoteDescAnswerTime = DateTime.UtcNow - setRemoteDescAnswerStart;
                    _debugLog.LogWebRtc($"[PERF] setRemoteDescription(answer) for {peerId}: {setRemoteDescAnswerTime.TotalMilliseconds:F1}ms");
                    
                    if (result != SetDescriptionResultEnum.OK)
                    { 
                        _debugLog.LogWebRtc($"Failed to set remote description from answer for {peerId}: {result}");
                        RemovePeerConnection(peerId); 
                    }
                    else
                    {
                        _debugLog.LogWebRtc($"Set remote description from answer for {peerId} successfully.");
                        var totalAnswerProcessingTime = DateTime.UtcNow - answerProcessingStart;
                        _debugLog.LogWebRtc($"[PERF] TOTAL ANSWER PROCESSING TIME for {peerId}: {totalAnswerProcessingTime.TotalMilliseconds:F1}ms");
                    }
                }
                catch (Exception ex)
                {
                    _debugLog.LogWebRtc($"Error processing answer from {peerId}: {ex.Message}");
                    RemovePeerConnection(peerId);
                }
            }
            }
            catch (Exception ex)
            {
                _debugLog.LogWebRtc($"CRITICAL: Unhandled exception in OnAnswerReceived for {payload.SenderId}: {ex.Message}");
                _debugLog.LogWebRtc($"Stack trace: {ex.StackTrace}");
                
                // Ensure cleanup happens
                if (payload.SenderId != null)
                {
                    RemovePeerConnection(payload.SenderId);
                }
            }
        });
    }

    private void OnIceCandidateReceived(object? sender, IceCandidatePayload payload)
    {
        if (payload.SenderId == null || payload.Candidate == null) return;

        var peerId = payload.SenderId;
        if (!_peerConnections.TryGetValue(peerId, out var state)) return;

        try
        {
            var candidate = JsonConvert.DeserializeObject<RTCIceCandidateInit>(payload.Candidate);
            if (candidate == null) return;

            state.PeerConnection.addIceCandidate(candidate);
        }
        catch (Exception ex)
        {
            _debugLog.LogWebRtc($"CRITICAL: Unhandled exception in OnIceCandidateReceived for {peerId}: {ex.Message}");
            _debugLog.LogWebRtc($"Stack trace: {ex.StackTrace}");
            
            // Clean up the problematic peer connection
            RemovePeerConnection(peerId);
        }
    }

    public void RemovePeerConnection(string peerId)
    {
        if (_peerConnections.TryRemove(peerId, out var state))
        {
            try
            {
                // Clean up heartbeat tracking
                _lastHeartbeatReceived.TryRemove(peerId, out _);
                
                // Disconnect VirtualAudioSource from this peer connection
                if (_virtualAudioSource != null)
                {
                    _virtualAudioSource.OnAudioSourceEncodedSample -= state.PeerConnection.SendAudio;
                    _debugLog.LogWebRtc($"Disconnected VirtualAudioSource from peer connection {peerId}");
                }
                
                state.PeerConnection.Close("Removed by application logic");
            }
            catch (Exception ex)
            {
                _debugLog.LogWebRtc($"Error closing peer connection for {peerId}: {ex.Message}");
            }
        }
    }

    public void SendPosition(string peerId, int mapId, int x, int y, string characterName)
    {
        if (_peerConnections.TryGetValue(peerId, out var state))
        {
            if (state.DataChannel == null)
            {
                _debugLog.LogWebRtc($"Cannot send position to {peerId}: DataChannel is null");
                return;
            }
            
            if (state.DataChannel.readyState != RTCDataChannelState.open)
            {
                _debugLog.LogWebRtc($"Cannot send position to {peerId}: DataChannel state is {state.DataChannel.readyState}, expected open");
                return;
            }
            
            try
            {
                var positionData = new PositionData { MapId = mapId, X = x, Y = y, CharacterName = characterName };
                var json = System.Text.Json.JsonSerializer.Serialize(positionData);
                state.DataChannel.send(json);
                
                // removed excessive position send logging - only log errors
            }
            catch (Exception ex)
            {
                _debugLog.LogWebRtc($"Error sending position to {peerId}: {ex.Message}");
            }
        }
        else
        {
            _debugLog.LogWebRtc($"Cannot send position to {peerId}: peer connection not found");
        }
    }

    // Send position update to all connected peers
    public void SendPositionToAllPeers(int mapId, int x, int y, string characterName)
    {
        // removed excessive position update logging
        foreach (var peerId in _peerConnections.Keys)
        {
            SendPosition(peerId, mapId, x, y, characterName);
        }
    }

    public void UpdatePeerVolume(string peerId, float distance)
    {
         _debugLog.LogWebRtc($"UpdatePeerVolume is not fully implemented for SIPSorceryMedia yet.");
    }

    private void SendHeartbeats(object? timerState)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var heartbeat = new HeartbeatMessage { Timestamp = timestamp };
            var json = System.Text.Json.JsonSerializer.Serialize(heartbeat);
            
            var peersToRemove = new List<string>();
            var now = DateTime.UtcNow;
            
            foreach (var kvp in _peerConnections)
            {
                var peerId = kvp.Key;
                var peerState = kvp.Value;
                
                // Check for heartbeat timeout
                if (_lastHeartbeatReceived.TryGetValue(peerId, out var lastHeartbeat))
                {
                    if (now - lastHeartbeat > _heartbeatTimeout)
                    {
                        _debugLog.LogWebRtc($"Heartbeat timeout for peer {peerId} - last heartbeat {(now - lastHeartbeat).TotalSeconds:F1}s ago");
                        peersToRemove.Add(peerId);
                        continue;
                    }
                }
                else
                {
                    // First heartbeat - initialize tracking
                    _lastHeartbeatReceived[peerId] = now;
                }
                
                // Send heartbeat if data channel is ready
                if (peerState.DataChannel != null && peerState.DataChannel.readyState == RTCDataChannelState.open)
                {
                    try
                    {
                        peerState.DataChannel.send(json);
                    }
                    catch (Exception ex)
                    {
                        _debugLog.LogWebRtc($"Error sending heartbeat to {peerId}: {ex.Message}");
                        peersToRemove.Add(peerId);
                    }
                }
            }
            
            // Remove timed out peers
            foreach (var peerId in peersToRemove)
            {
                _debugLog.LogWebRtc($"Removing peer {peerId} due to heartbeat timeout or send error");
                RemovePeerConnection(peerId);
            }
        }
        catch (Exception ex)
        {
            _debugLog.LogWebRtc($"Error in SendHeartbeats: {ex.Message}");
        }
    }

    public void CloseAllConnections()
    {
        var peerIds = _peerConnections.Keys.ToList();
        foreach (var peerId in peerIds)
        {
            RemovePeerConnection(peerId);
        }
        _peerConnections.Clear();
    }

    public void Dispose()
    {
        // Stop heartbeat timer first  
        _heartbeatTimer?.Dispose();
        
        CloseAllConnections();

        _signalingService.OfferReceived -= OnOfferReceived;
        _signalingService.AnswerReceived -= OnAnswerReceived;
        _signalingService.IceCandidateReceived -= OnIceCandidateReceived;
        
        if (_virtualAudioSource != null)
        {
            try
            {
                _virtualAudioSource.CloseAudio().Wait();
            }
            catch (Exception ex)
            {
                _debugLog.LogWebRtc($"Error closing VirtualAudioSource: {ex.Message}");
            }
        }

        // CHANGE: Clean up semaphore resource
        _connectionSemaphore?.Dispose();

        GC.SuppressFinalize(this);
    }
}

// Helper class to hold state associated with each peer connection
internal class PeerConnectionState
{
    public RTCPeerConnection PeerConnection { get; set; } = null!;
    public RTCDataChannel? DataChannel { get; set; }
    // public uint RemoteAudioSsrc { get; set; } // Removed: No longer needed by application logic for routing
    // Add other relevant state if needed, e.g., audio stream references if managed separately
} 