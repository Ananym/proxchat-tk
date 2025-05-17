using System;
using System.Collections.Concurrent;
using System.Linq; // Added for First()
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using ProxChatClient.Models.Signaling;
using ProxChatClient.Services;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;
using System.Diagnostics; // Keep for Debug.WriteLine
using System.IO; // For file logging

namespace ProxChatClient.Services;

public class WebRtcService : IDisposable
{
    private readonly ILogger<WebRtcService> _logger;
    private readonly ConcurrentDictionary<string, PeerConnectionState> _peerConnections = new();
    private readonly SignalingService _signalingService;
    private readonly AudioService _audioService;
    private readonly float _maxDistance;
    private readonly DebugLogService _debugLog;
    private bool _isInitialized;

    private static readonly RTCIceServer _stunServer = new RTCIceServer { urls = "stun:stun.l.google.com:19302" };
    private WindowsAudioEndPoint? _audioEndPoint;
    private MediaStreamTrack? _audioTrack;

    public event EventHandler<(string PeerId, int MapId, int X, int Y, string CharacterName)>? PositionReceived;
    public event EventHandler<string>? DataChannelOpened;

    private class PositionData
    {
        public int MapId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public string CharacterName { get; set; } = "";
    }

    public WebRtcService(AudioService audioService, SignalingService signalingService, float maxDistance, DebugLogService debugLog, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<WebRtcService>() ?? NullLogger<WebRtcService>.Instance;
        _audioService = audioService;
        _signalingService = signalingService;
        _maxDistance = maxDistance;
        _debugLog = debugLog;
        _isInitialized = false;
    }

    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            _debugLog.LogWebRtc("WebRtcService initialization started");
            
            InitializeAudioSystem();

            _signalingService.OfferReceived += OnOfferReceived;
            _signalingService.AnswerReceived += OnAnswerReceived;
            _signalingService.IceCandidateReceived += OnIceCandidateReceived;

            _isInitialized = true;
            _debugLog.LogWebRtc("WebRtcService initialization completed");
            _logger.LogInformation("WebRtcService initialized.");
        }
    }

    private void InitializeAudioSystem()
    {
        _debugLog.LogWebRtc("InitializeAudioSystem started");
        try
        {
            // Use the AudioEncoder constructor without parameters
            _audioEndPoint = new WindowsAudioEndPoint(new AudioEncoder()); 
            _debugLog.LogWebRtc("WindowsAudioEndPoint created");
            
            // Define the audio format explicitly
            var audioFormat = new AudioFormat(AudioCodecsEnum.PCMU, 0); // Using PCMU format ID 0
            _audioTrack = new MediaStreamTrack(audioFormat, MediaStreamStatusEnum.SendRecv);
            _debugLog.LogWebRtc("MediaStreamTrack created");

            _audioEndPoint.StartAudio(); 
            _debugLog.LogWebRtc("Audio started successfully");

            _logger.LogInformation("Windows Audio Endpoint initialized.");
        }
        catch (Exception ex)
        {
            _debugLog.LogWebRtc($"InitializeAudioSystem failed: {ex.Message}");
            _logger.LogError(ex, "Failed to initialize Windows Audio Endpoint.");
            throw;
        }
    }

    public async Task CreatePeerConnection(string peerId, bool isInitiator)
    {
        EnsureInitialized();
        _debugLog.LogWebRtc($"CreatePeerConnection START - PeerId: {peerId}, Initiator: {isInitiator}");
        
        if (_peerConnections.ContainsKey(peerId)) 
        {
            _debugLog.LogWebRtc($"CreatePeerConnection EARLY EXIT - peer {peerId} already exists");
            return;
        }
        
        if (_audioTrack == null) 
        { 
            _debugLog.LogWebRtc("CreatePeerConnection EARLY EXIT - audio track not initialized");
            _logger.LogWarning("Cannot create peer connection, audio track not initialized.");
            return; 
        }

        _logger.LogInformation("Creating peer connection to {PeerId}. Initiator: {IsInitiator}", peerId, isInitiator);

        _debugLog.LogWebRtc($"Creating RTCPeerConnection for {peerId}");
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer> { _stunServer } });
        _debugLog.LogWebRtc($"RTCPeerConnection created for {peerId}");

        var state = new PeerConnectionState { PeerConnection = pc };
        if (!_peerConnections.TryAdd(peerId, state))
        {
            _debugLog.LogWebRtc($"Failed to add {peerId} to peer connections dictionary");
            _logger.LogWarning("Failed to add peer connection {PeerId} to dictionary.", peerId);
            pc.Close("Failed to add to dictionary");
            return;
        }
        _debugLog.LogWebRtc($"Added {peerId} to peer connections dictionary");

        // --- Media Tracks ---
        _debugLog.LogWebRtc($"Adding audio track for {peerId}");
        pc.addTrack(_audioTrack);
        _debugLog.LogWebRtc($"Audio track added for {peerId}");

        // Hook up the RTP packet received event to the audio end point
        pc.OnRtpPacketReceived += (ep, mediaType, rtpPacket) => 
        {
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                _audioEndPoint?.GotAudioRtp(ep, 
                    rtpPacket.Header.SyncSource, 
                    rtpPacket.Header.SequenceNumber, 
                    rtpPacket.Header.Timestamp,
                    rtpPacket.Header.PayloadType,
                    rtpPacket.Header.MarkerBit == 1,
                    rtpPacket.Payload); 
            }
        };
        _debugLog.LogWebRtc($"RTP packet handler set for {peerId}");

        // --- Data Channel ---
        _debugLog.LogWebRtc($"Creating data channel for {peerId}");
        var dataChannel = await pc.createDataChannel("position").ConfigureAwait(false); 
        _debugLog.LogWebRtc($"Data channel creation completed for {peerId}, result: {(dataChannel != null ? "SUCCESS" : "FAILED")}");
        
        if (dataChannel == null)
        {
            _debugLog.LogWebRtc($"Data channel creation FAILED for {peerId} - removing peer connection");
            _logger.LogError("Failed to create data channel for {PeerId}", peerId);
             RemovePeerConnection(peerId);
             return;
        }
        
        _debugLog.LogWebRtc($"Configuring data channel for {peerId}");
        ConfigureDataChannel(peerId, dataChannel);
        _debugLog.LogWebRtc($"Data channel configured for {peerId}");
        
        pc.ondatachannel += (dc) =>
        {
            _debugLog.LogWebRtc($"Data channel RECEIVED from {peerId}: {dc.label}");
            _logger.LogInformation("Data channel received from {PeerId}: {Label}", peerId, dc.label);
            ConfigureDataChannel(peerId, dc);
        };

        // --- ICE Candidates ---
        _debugLog.LogWebRtc($"Setting up ICE candidate handler for {peerId}");
        pc.onicecandidate += (candidate) =>
        {
            if (candidate != null)
            {
                _debugLog.LogWebRtc($"ICE candidate generated for {peerId}: {candidate.candidate}");
                // Fire and forget to avoid blocking
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var candidateJson = candidate.toJSON(); // Use SIPSorcery method
                        _debugLog.LogWebRtc($"Sending ICE candidate for {peerId}");
                        await _signalingService.SendIceCandidateAsync(peerId, candidateJson).ConfigureAwait(false);
                        _debugLog.LogWebRtc($"ICE candidate sent successfully for {peerId}");
                        _logger.LogTrace("Sent ICE candidate to {PeerId}: {Candidate}", peerId, candidate.candidate);
                    }
                    catch (Exception ex)
                    {
                        _debugLog.LogWebRtc($"Error sending ICE candidate for {peerId}: {ex.Message}");
                        _logger.LogError(ex, "Error sending ICE candidate to {PeerId}", peerId);
                    }
                });
            }
        };

        // --- Connection State ---
        _debugLog.LogWebRtc($"Setting up connection state handler for {peerId}");
        pc.onconnectionstatechange += (connState) =>
        {
            _debugLog.LogWebRtc($"Connection state changed for {peerId}: {connState}");
            _logger.LogInformation("Peer connection state for {PeerId} changed to {State}", peerId, connState);
            if (connState == RTCPeerConnectionState.failed || 
                connState == RTCPeerConnectionState.disconnected || 
                connState == RTCPeerConnectionState.closed)
            {
                _debugLog.LogWebRtc($"Connection failed/disconnected/closed for {peerId} - removing");
                _logger.LogWarning("Peer {PeerId} disconnected/failed/closed ({State}). Removing.", peerId, connState);
                RemovePeerConnection(peerId);
            }
        };

        // --- Offer Creation (if initiator) ---
        if (isInitiator)
        {
            _debugLog.LogWebRtc($"Creating offer for {peerId} (is initiator)");
            try
            {
                _debugLog.LogWebRtc($"About to call pc.createOffer() for {peerId}");
                
                // Run createOffer with a timeout to prevent hanging
                var offerTask = Task.Run(() => 
                {
                    _debugLog.LogWebRtc($"Inside Task.Run for createOffer for {peerId}");
                    var offer = pc.createOffer();
                    _debugLog.LogWebRtc($"createOffer completed for {peerId}");
                    return offer;
                });
                
                var offer = offerTask.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                
                _debugLog.LogWebRtc($"Offer created for {peerId}, setting local description");
                Task.Run(async () => await pc.setLocalDescription(offer)).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _debugLog.LogWebRtc($"Local description set for {peerId}, sending offer");
                _signalingService.SendOfferAsync(peerId, offer.toJSON()).GetAwaiter().GetResult();
                _debugLog.LogWebRtc($"Offer sent successfully for {peerId}");
                _logger.LogInformation("Sent offer to {PeerId}", peerId);
            }
            catch (TimeoutException ex)
            {
                _debugLog.LogWebRtc($"TIMEOUT creating offer for {peerId}: {ex.Message}");
                _logger.LogError("Timeout creating offer for {PeerId}", peerId);
                RemovePeerConnection(peerId);
            }
            catch (Exception ex)
            {
                _debugLog.LogWebRtc($"Error creating/sending offer for {peerId}: {ex.Message}");
                _logger.LogError(ex, "Error creating/sending offer to {PeerId}", peerId);
                RemovePeerConnection(peerId);
            }
        }
        
        _debugLog.LogWebRtc($"CreatePeerConnection COMPLETED - PeerId: {peerId}");
    }

    private void ConfigureDataChannel(string peerId, RTCDataChannel dc)
    {
        _debugLog.LogWebRtc($"ConfigureDataChannel START for {peerId}");
        
        // Need to get the state object correctly
        if (peerId != null && _peerConnections.TryGetValue(peerId, out var state))
        {
            state.DataChannel = dc; 
            _debugLog.LogWebRtc($"Data channel assigned to state for {peerId}");
            
            dc.onmessage += (channel, type, data) =>
            {
                _debugLog.LogWebRtc($"Data channel message received from {peerId}, length: {data?.Length}");
                // Handle message processing in background task to avoid blocking
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (data == null)
                        {
                            _debugLog.LogWebRtc($"Received null data from {peerId}");
                            return;
                        }
                        string message = Encoding.UTF8.GetString(data);
                        _debugLog.LogWebRtc($"Parsed data channel message from {peerId}: {message}");

                        // Deserialize to a concrete class
                        var posData = JsonConvert.DeserializeObject<PositionData>(message);
                        if (posData == null)
                        {
                            _debugLog.LogWebRtc($"Failed to parse position data from {peerId}");
                            return;
                        }

                        // Debug the values before invoking
                        _debugLog.LogWebRtc($"Before invoke - PeerId: {peerId}, MapId: {posData.MapId}, X: {posData.X}, Y: {posData.Y}, Name: {posData.CharacterName}");

                        // Create tuple for the event
                        var eventData = (peerId, posData.MapId, posData.X, posData.Y, posData.CharacterName);
                        _debugLog.LogWebRtc($"Event tuple - PeerId: {eventData.Item1}, MapId: {eventData.Item2}, X: {eventData.Item3}, Y: {eventData.Item4}, Name: {eventData.Item5}");

                        PositionReceived?.Invoke(this, eventData);
                        _debugLog.LogWebRtc($"Position data processed for {peerId}");
                    }
                    catch (Exception ex)
                    {
                        _debugLog.LogWebRtc($"Error parsing position data from {peerId}: {ex.Message}");
                        _logger.LogWarning(ex, "Failed to parse position data from {PeerId}", peerId);
                    }
                });
            };
            
            dc.onopen += () => {
                _debugLog.LogWebRtc($"Data channel OPENED for {peerId}");
                _logger.LogInformation("Data channel to {PeerId} opened.", peerId);
                
                // IMPROVEMENT: Immediately send our position data once data channel opens
                _ = Task.Run(() => {
                    try {
                        // Raise an event to notify that this connection needs a position update
                        // We'll define a new event for this
                        DataChannelOpened?.Invoke(this, peerId);
                        _debugLog.LogWebRtc($"DataChannelOpened event fired for {peerId}");
                    }
                    catch (Exception ex) {
                        _debugLog.LogWebRtc($"Error in data channel onopen handler for {peerId}: {ex.Message}");
                    }
                });
            };
            
            dc.onclose += () => {
                _debugLog.LogWebRtc($"Data channel CLOSED for {peerId}");
                _logger.LogInformation("Data channel to {PeerId} closed.", peerId);
            };
        }
        else
        {
            _debugLog.LogWebRtc($"ConfigureDataChannel FAILED - could not find state for {peerId}");
        }
    }

    private void OnOfferReceived(object? sender, OfferPayload payload)
    {
        _debugLog.LogWebRtc($"OnOfferReceived START - SenderId: {payload.SenderId}");
        
        // Fire and forget to avoid blocking signaling service
        _ = Task.Run(() =>
        {
            var peerId = payload.SenderId;
            if (peerId == null || payload.Offer == null)
            {
                _debugLog.LogWebRtc("OnOfferReceived EARLY EXIT - null sender ID or offer");
                _logger.LogWarning("Received offer with null sender ID or null offer payload.");
                return;
            }

            _debugLog.LogWebRtc($"Processing offer from {peerId}");
            _logger.LogInformation("Received offer from {PeerId}", peerId);

            RTCPeerConnection pc;
            if (!_peerConnections.TryGetValue(peerId, out var state))
            {
                _debugLog.LogWebRtc($"No existing connection for {peerId}, creating new one as non-initiator");
                // Connection doesn't exist, create it as non-initiator
                CreatePeerConnection(peerId, isInitiator: false).GetAwaiter().GetResult();
                _debugLog.LogWebRtc($"CreatePeerConnection completed for {peerId}");
                
                if (!_peerConnections.TryGetValue(peerId, out state))
                {
                    _debugLog.LogWebRtc($"CRITICAL ERROR: Failed to create peer connection for {peerId}");
                    _logger.LogError("Failed to create peer connection for offer from {PeerId}. Aborting.", peerId);
                    return;
                }
                pc = state.PeerConnection;
                _debugLog.LogWebRtc($"Retrieved peer connection for {peerId} after creation");
            }
            else
            {
                _debugLog.LogWebRtc($"Using existing connection for {peerId}");
                pc = state.PeerConnection;
            }

            try
            {
                _debugLog.LogWebRtc($"Deserializing offer SDP for {peerId}");
                // Use JsonConvert.DeserializeObject to parse SDP string
                var offerSdp = JsonConvert.DeserializeObject<RTCSessionDescriptionInit>(payload.Offer); 
                if (offerSdp == null) 
                {
                    _debugLog.LogWebRtc($"Failed to deserialize offer SDP for {peerId}");
                    _logger.LogWarning("Failed to deserialize offer SDP from {PeerId}", peerId);
                    return;
                }
                
                _debugLog.LogWebRtc($"Setting remote description (offer) for {peerId}");
                var result = pc.setRemoteDescription(offerSdp);
                _debugLog.LogWebRtc($"setRemoteDescription result for {peerId}: {result}");
                
                if (result != SetDescriptionResultEnum.OK)
                {
                    _debugLog.LogWebRtc($"setRemoteDescription FAILED for {peerId}: {result}");
                    _logger.LogError("Failed to set remote description (offer) for {PeerId}. Result: {Result}", peerId, result);
                    RemovePeerConnection(peerId);
                    return;
                }

                _debugLog.LogWebRtc($"Creating answer for {peerId}");
                var answerTask = Task.Run(() => 
                {
                    _debugLog.LogWebRtc($"Inside Task.Run for createAnswer for {peerId}");
                    var answer = pc.createAnswer();
                    _debugLog.LogWebRtc($"createAnswer completed for {peerId}");
                    return answer;
                });

                var answer = answerTask.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

                _debugLog.LogWebRtc($"Answer created for {peerId}, setting local description");
                Task.Run(async () => await pc.setLocalDescription(answer)).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _debugLog.LogWebRtc($"Local description (answer) set for {peerId}, sending to signaling");

                _signalingService.SendAnswerAsync(peerId, answer.toJSON()).GetAwaiter().GetResult();
                _debugLog.LogWebRtc($"Answer sent successfully for {peerId}");
                _logger.LogInformation("Sent answer to {PeerId}", peerId);
            }
            catch (Exception ex)
            {
                _debugLog.LogWebRtc($"ERROR in OnOfferReceived for {peerId}: {ex.Message}");
                _debugLog.LogWebRtc($"Stack trace: {ex.StackTrace}");
                _logger.LogError(ex, "Error handling offer/creating answer for {PeerId}", peerId);
                RemovePeerConnection(peerId);
            }
        });
        
        _debugLog.LogWebRtc($"OnOfferReceived END (task started) - SenderId: {payload.SenderId}");
    }

    private void OnAnswerReceived(object? sender, AnswerPayload payload)
    {
        _debugLog.LogWebRtc($"OnAnswerReceived START - SenderId: {payload.SenderId}");
        
        // Fire and forget to avoid blocking signaling service
        _ = Task.Run(() =>
        {
            var peerId = payload.SenderId;
            if (peerId == null || payload.Answer == null)
            {
                _debugLog.LogWebRtc("OnAnswerReceived EARLY EXIT - null sender ID or answer");
                _logger.LogWarning("Received answer with null sender ID or null answer payload.");
                return;
            }

            _debugLog.LogWebRtc($"Processing answer from {peerId}");
            _logger.LogInformation("Received answer from {PeerId}", peerId);

            if (_peerConnections.TryGetValue(peerId, out var state))
            {
                try
                {
                    _debugLog.LogWebRtc($"Deserializing answer SDP for {peerId}");
                    // Use JsonConvert.DeserializeObject to parse SDP string
                    var answerSdp = JsonConvert.DeserializeObject<RTCSessionDescriptionInit>(payload.Answer); 
                    if (answerSdp == null)
                    {
                        _debugLog.LogWebRtc($"Failed to deserialize answer SDP for {peerId}");
                        _logger.LogWarning("Failed to deserialize answer SDP from {PeerId}", peerId);
                        return;
                    }
                    
                    _debugLog.LogWebRtc($"Setting remote description (answer) for {peerId}");
                    var result = state.PeerConnection.setRemoteDescription(answerSdp);
                    _debugLog.LogWebRtc($"setRemoteDescription (answer) result for {peerId}: {result}");
                    
                    if (result != SetDescriptionResultEnum.OK)
                    { 
                        _debugLog.LogWebRtc($"setRemoteDescription (answer) FAILED for {peerId}: {result}");
                        _logger.LogError("Failed to set remote description (answer) for {PeerId}. Result: {Result}", peerId, result);
                        RemovePeerConnection(peerId); 
                    }
                    else
                    {
                        _debugLog.LogWebRtc($"Successfully set remote description (answer) for {peerId}");
                        _logger.LogInformation("Successfully set remote description (answer) for {PeerId}", peerId);
                    }
                }
                catch (Exception ex)
                {
                    _debugLog.LogWebRtc($"ERROR in OnAnswerReceived for {peerId}: {ex.Message}");
                    _logger.LogError(ex, "Error setting remote description (answer) for {PeerId}", peerId);
                    RemovePeerConnection(peerId);
                }
            }
            else
            {
                _debugLog.LogWebRtc($"OnAnswerReceived - unknown peer: {peerId}");
                _logger.LogWarning("Received answer for unknown peer: {PeerId}", peerId);
            }
        });
        
        _debugLog.LogWebRtc($"OnAnswerReceived END (task started) - SenderId: {payload.SenderId}");
    }

    private void OnIceCandidateReceived(object? sender, IceCandidatePayload payload)
    {
        _debugLog.LogWebRtc($"OnIceCandidateReceived START - SenderId: {payload.SenderId}");
        
        // Fire and forget to avoid blocking signaling service
        _ = Task.Run(() =>
        {
            var peerId = payload.SenderId;
            if (peerId == null || payload.Candidate == null)
            {
                _debugLog.LogWebRtc("OnIceCandidateReceived EARLY EXIT - null sender ID or candidate");
                _logger.LogWarning("Received ICE candidate with null sender ID or null candidate payload.");
                return;
            }

            _debugLog.LogWebRtc($"Processing ICE candidate from {peerId}");

            if (_peerConnections.TryGetValue(peerId, out var state))
            {
                try
                {
                    _debugLog.LogWebRtc($"Deserializing ICE candidate for {peerId}");
                    // Use JsonConvert.DeserializeObject to parse ICE candidate string
                    var candidateInit = JsonConvert.DeserializeObject<RTCIceCandidateInit>(payload.Candidate); 
                    if (candidateInit == null)
                    {
                        _debugLog.LogWebRtc($"Failed to deserialize ICE candidate for {peerId}");
                        _logger.LogWarning("Failed to deserialize ICE candidate from {PeerId}", peerId);
                        return;
                    }
                    
                    _debugLog.LogWebRtc($"Adding ICE candidate for {peerId}: {candidateInit.candidate}");
                    state.PeerConnection.addIceCandidate(candidateInit);
                    _debugLog.LogWebRtc($"ICE candidate added successfully for {peerId}");
                    _logger.LogTrace("Added ICE candidate from {PeerId}", peerId);
                }
                catch (Exception ex)
                {
                    _debugLog.LogWebRtc($"Error adding ICE candidate for {peerId}: {ex.Message}");
                    _logger.LogError(ex, "Error adding ICE candidate from {PeerId}", peerId);
                }
            }
            else
            {
                _debugLog.LogWebRtc($"OnIceCandidateReceived - unknown peer: {peerId}");
                _logger.LogWarning("Received ICE candidate for unknown peer: {PeerId}", peerId);
            }
        });
        
        _debugLog.LogWebRtc($"OnIceCandidateReceived END (task started) - SenderId: {payload.SenderId}");
    }

    public void RemovePeerConnection(string peerId)
    {
        _debugLog.LogWebRtc($"RemovePeerConnection START for {peerId}");
        
        if (_peerConnections.TryRemove(peerId, out var state))
        {
            _debugLog.LogWebRtc($"Removing peer connection for {peerId}");
            _logger.LogInformation("Removing peer connection for {PeerId}", peerId);
            try
            {
                state.PeerConnection.Close("Removed by application logic");
                _debugLog.LogWebRtc($"Peer connection closed successfully for {peerId}");
            }
            catch (Exception ex)
            {
                _debugLog.LogWebRtc($"Error closing peer connection for {peerId}: {ex.Message}");
                _logger.LogError(ex, "Error closing peer connection for {PeerId}", peerId);
            }
        }
        else
        {
            _debugLog.LogWebRtc($"RemovePeerConnection - peer {peerId} not found in dictionary");
        }
    }

    public void SendPosition(string peerId, int mapId, int x, int y, string characterName)
    {
        if (_peerConnections.TryGetValue(peerId, out var state) && state.DataChannel?.readyState == RTCDataChannelState.open)
        {
            try
            {
                var positionData = new { MapId = mapId, X = x, Y = y, CharacterName = characterName };
                string message = JsonConvert.SerializeObject(positionData);
                state.DataChannel.send(Encoding.UTF8.GetBytes(message));
                _logger.LogTrace("Sent position data to {PeerId}", peerId);
            }
            catch (Exception ex)
            {
                _debugLog.LogWebRtc($"Error sending position to {peerId}: {ex.Message}");
                _logger.LogWarning(ex, "Error sending position data to {PeerId}", peerId);
            }
        }
    }

    public void UpdatePeerVolume(string peerId, float distance)
    {
         _logger.LogWarning("UpdatePeerVolume is not fully implemented for SIPSorceryMedia yet.");
    }

    public void CloseAllConnections()
    {
        _debugLog.LogWebRtc("CloseAllConnections START");
        _logger.LogInformation("Closing all peer connections.");
        var peerIds = _peerConnections.Keys.ToList();
        foreach (var peerId in peerIds)
        {
            RemovePeerConnection(peerId);
        }
        _peerConnections.Clear();
        _debugLog.LogWebRtc("CloseAllConnections COMPLETED");
    }

    public void Dispose()
    {
        _debugLog.LogWebRtc("WebRtcService.Dispose START");
        _logger.LogInformation("Disposing WebRtcService.");
        CloseAllConnections();

        _signalingService.OfferReceived -= OnOfferReceived;
        _signalingService.AnswerReceived -= OnAnswerReceived;
        _signalingService.IceCandidateReceived -= OnIceCandidateReceived;
        
        // Correct method: CloseAudio
        _audioEndPoint?.CloseAudio(); 

        _logger.LogInformation("WebRtcService disposed.");
        GC.SuppressFinalize(this);
    }
}

// Helper class to hold state associated with each peer connection
internal class PeerConnectionState
{
    public RTCPeerConnection PeerConnection { get; set; } = null!;
    public RTCDataChannel? DataChannel { get; set; }
    // Add other relevant state if needed, e.g., audio stream references if managed separately
} 