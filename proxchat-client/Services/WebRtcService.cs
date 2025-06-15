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

    private static readonly RTCIceServer _stunServer = new RTCIceServer { urls = "stun:stun.l.google.com:19302" };
    private AudioExtrasSource? _audioExtrasSource;
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

    public WebRtcService(AudioService audioService, SignalingService signalingService, float maxDistance, DebugLogService debugLog)
    {
        _audioService = audioService;
        _signalingService = signalingService;
        _maxDistance = maxDistance;
        _debugLog = debugLog;
        _isInitialized = false;

        // Initialize audio system first
        InitializeAudioSystem();

        // Connect our AudioService to the audio aggregator
        _audioService.EncodedAudioPacketAvailable += OnAudioPacketFromService;

        // Initialize immediately
        EnsureInitialized();
    }

    private void EnsureInitialized()
    {
        if (_isInitialized) return;

        try
        {
            // Subscribe to signaling events
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
        try
        {
            // Clean up existing resources
            if (_audioExtrasSource != null)
            {
                _audioExtrasSource.CloseAudio();
                _audioExtrasSource = null;
            }

            // Create AudioExtrasSource as our audio aggregator
            _audioExtrasSource = new AudioExtrasSource(new AudioEncoder());
            _debugLog.LogWebRtc("Created AudioExtrasSource as audio aggregator");
            
            // Create media track with the AudioExtrasSource
            _audioTrack = new MediaStreamTrack(_audioExtrasSource.GetAudioSourceFormats(), MediaStreamStatusEnum.SendOnly);
            _debugLog.LogWebRtc($"Created audio track with AudioExtrasSource");

            // AudioExtrasSource will automatically handle audio distribution through the MediaStreamTrack
            // No need for manual SendAudio calls - the track integration handles this

            // Start the audio source
            _audioExtrasSource.StartAudio();
            _debugLog.LogWebRtc("AudioExtrasSource started and connected to track");
        }
        catch (Exception ex)
        {
            _debugLog.LogWebRtc($"Error initializing audio system with AudioExtrasSource: {ex.Message}");
            
            // Clean up any partially initialized resources
            if (_audioExtrasSource != null)
            {
                try
                {
                    _audioExtrasSource.CloseAudio();
                }
                catch { }
                _audioExtrasSource = null;
            }
            _audioTrack = null;
            
            throw;
        }
    }

    private void OnAudioPacketFromService(object? sender, EncodedAudioPacketEventArgs e)
    {
        // Bridge our AudioService packets to AudioExtrasSource
        if (_audioExtrasSource != null)
        {
            try
            {
                // AudioExtrasSource expects raw PCM samples via ExternalAudioSourceRawSample
                // We need to decode our Opus packets back to PCM first
                if (e.Buffer.Length > 0)
                {
                    // Decode Opus packet to PCM using AudioService's codec
                    short[] pcmSamples = _audioService.DecodeOpusPacket(e.Buffer, e.Buffer.Length);
                    
                    if (pcmSamples.Length > 0)
                    {
                        // Feed raw PCM samples to AudioExtrasSource
                        // Duration is 20ms for standard Opus frames
                        _audioExtrasSource.ExternalAudioSourceRawSample(
                            SIPSorceryMedia.Abstractions.AudioSamplingRatesEnum.Rate48KHz, 
                            20, // 20ms duration
                            pcmSamples);
                    }
                }
                else
                {
                    // Send silence for empty packets
                    short[] silenceSamples = new short[960]; // 20ms at 48kHz = 960 samples
                    Array.Fill(silenceSamples, (short)0);
                    _audioExtrasSource.ExternalAudioSourceRawSample(
                        SIPSorceryMedia.Abstractions.AudioSamplingRatesEnum.Rate48KHz, 
                        20, 
                        silenceSamples);
                }
            }
            catch (Exception ex)
            {
                _debugLog.LogWebRtc($"Error forwarding audio packet to AudioExtrasSource: {ex.Message}");
            }
        }
    }

    public async Task CreatePeerConnection(string peerId, bool isInitiator)
    {
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

        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer> { _stunServer } });
        _debugLog.LogWebRtc($"Created new peer connection for {peerId}");

        var state = new PeerConnectionState { PeerConnection = pc };
        if (!_peerConnections.TryAdd(peerId, state))
        {
            pc.Close("Failed to add to dictionary");
            return;
        }

        try
        {
            // --- Media Tracks ---
            pc.addTrack(_audioTrack);
            _debugLog.LogWebRtc($"Added audio track to peer connection {peerId}");

            // Set up format negotiation for the audio source
            pc.OnAudioFormatsNegotiated += (formats) =>
            {
                if (_audioExtrasSource != null && formats.Any())
                {
                    _audioExtrasSource.SetAudioSourceFormat(formats.First());
                    _debugLog.LogWebRtc($"Set audio format for peer {peerId}: {formats.First().Codec}");
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
                _debugLog.LogWebRtc($"Peer connection state changed for {peerId}: {connState}");
                if (connState == RTCPeerConnectionState.connected)
                {
                    _debugLog.LogWebRtc($"WebRTC connection established with {peerId}");
                    _debugLog.LogWebRtc($"[AUDIO_FLOW] Peer {peerId} connected - will now receive audio packets if any are being generated");
                }
                else if (connState == RTCPeerConnectionState.failed)
                {
                    _debugLog.LogWebRtc($"WebRTC connection failed for {peerId}, requesting peer refresh");
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

            // Data Channel: Only the designated initiator explicitly creates the data channel.
            // The other peer will receive it via the ondatachannel event.
            if (isInitiator) // isInitiator from MainViewModel string comparison
            {
                var dataChannel = await pc.createDataChannel("position").ConfigureAwait(false);
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
                    RTCSessionDescriptionInit offer = await Task.Run(() => pc.createOffer());
                    _debugLog.LogWebRtc($"[INITIATOR LOG] pc.createOffer() completed for peer {peerId}. SDP: {offer.sdp}");

                    _debugLog.LogWebRtc($"Created offer for peer {peerId} with SDP: {offer.sdp}");

                    // Offerer sets its local description with the offer.
                    _debugLog.LogWebRtc($"[INITIATOR LOG] About to call pc.setLocalDescription(offer) for peer {peerId}.");
                    await pc.setLocalDescription(offer); // Async, Task (void)
                    _debugLog.LogWebRtc($"[INITIATOR LOG] pc.setLocalDescription(offer) completed for peer {peerId}.");

                    _debugLog.LogWebRtc($"Set local description with offer for peer {peerId}.");

                    _debugLog.LogWebRtc($"[INITIATOR LOG] About to call _signalingService.SendOfferAsync for peer {peerId}.");
                    await _signalingService.SendOfferAsync(peerId, offer.toJSON()).ConfigureAwait(false);
                    _debugLog.LogWebRtc($"[INITIATOR LOG] _signalingService.SendOfferAsync completed for peer {peerId}.");
                    
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
        }
        catch (Exception ex)
        {
            _debugLog.LogWebRtc($"Error in CreatePeerConnection for {peerId}: {ex.Message}");
            RemovePeerConnection(peerId);
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
                        // TEMP LOG: Received raw message
                        _debugLog.LogWebRtc($"[TEMP] DC Message From {peerId}: Raw='{message}'");
                        var posData = JsonConvert.DeserializeObject<PositionData>(message);
                        if (posData == null) return;

                        // TEMP LOG: Received deserialized position data
                        _debugLog.LogWebRtc($"[TEMP] DC Position From {peerId}: MapId={posData.MapId}, X={posData.X}, Y={posData.Y}, CharName='{posData.CharacterName}'");

                        var eventData = (peerId, posData.MapId, posData.X, posData.Y, posData.CharacterName);
                        PositionReceived?.Invoke(this, eventData);
                        _debugLog.LogWebRtc($"Received position update from {peerId}: MapId={posData.MapId}, X={posData.X}, Y={posData.Y}");
                    }
                    catch (Exception ex)
                    {
                        _debugLog.LogWebRtc($"Error parsing position data from {peerId}: {ex.Message}");
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
            var peerId = payload.SenderId;
            RTCPeerConnection pc;
            PeerConnectionState? state; // Declare here

            // Check if peer connection exists, if not create it
            if (!_peerConnections.TryGetValue(peerId, out state) || state == null)
            {
                // This client is the non-initiator, so create peer connection without data channel
                // The data channel will come via ondatachannel event when we set remote description
                await CreatePeerConnection(peerId, isInitiator: false).ConfigureAwait(false);
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
                var offerSdp = JsonConvert.DeserializeObject<RTCSessionDescriptionInit>(payload.Offer); 
                if (offerSdp == null) return;
                
                _debugLog.LogWebRtc($"Received offer from {peerId} with SDP: {offerSdp.sdp}");
                
                // Setting remote description will trigger ondatachannel event if data channel is in SDP
                var result = pc.setRemoteDescription(offerSdp); // Synchronous, returns SetDescriptionResultEnum
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

                var answer = pc.createAnswer(); // Synchronous
                _debugLog.LogWebRtc($"Created answer for {peerId} with SDP: {answer.sdp}");

                await pc.setLocalDescription(answer); // Async, Task (void)
                _debugLog.LogWebRtc($"Set local description with answer for {peerId}.");

                await _signalingService.SendAnswerAsync(peerId, answer.toJSON()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _debugLog.LogWebRtc($"Error processing offer from {peerId}: {ex.Message}");
                RemovePeerConnection(peerId);
            }
        });
    }

    private void OnAnswerReceived(object? sender, AnswerPayload payload)
    {
        if (payload.SenderId == null || payload.Answer == null) return;

        _ = Task.Run(() => // remove async since no await is used
        {
            var peerId = payload.SenderId;
            if (_peerConnections.TryGetValue(peerId, out var state) && state != null)
            {
                try
                {
                    var answerSdp = JsonConvert.DeserializeObject<RTCSessionDescriptionInit>(payload.Answer); 
                    if (answerSdp == null) return;
                    
                    _debugLog.LogWebRtc($"Received answer from {peerId} with SDP: {answerSdp.sdp}");
                    
                    var result = state.PeerConnection.setRemoteDescription(answerSdp); // Synchronous, returns SetDescriptionResultEnum
                    if (result != SetDescriptionResultEnum.OK)
                    { 
                        _debugLog.LogWebRtc($"Failed to set remote description from answer for {peerId}: {result}");
                        RemovePeerConnection(peerId); 
                    }
                    else
                    {
                        _debugLog.LogWebRtc($"Set remote description from answer for {peerId} successfully.");
                    }
                }
                catch (Exception ex)
                {
                    _debugLog.LogWebRtc($"Error processing answer from {peerId}: {ex.Message}");
                    RemovePeerConnection(peerId);
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
            _debugLog.LogWebRtc($"Error processing ICE candidate from {peerId}: {ex.Message}");
        }
    }

    public void RemovePeerConnection(string peerId)
    {
        if (_peerConnections.TryRemove(peerId, out var state))
        {
            try
            {
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
        CloseAllConnections();

        _signalingService.OfferReceived -= OnOfferReceived;
        _signalingService.AnswerReceived -= OnAnswerReceived;
        _signalingService.IceCandidateReceived -= OnIceCandidateReceived;
        _audioService.EncodedAudioPacketAvailable -= OnAudioPacketFromService;
        
        _audioExtrasSource?.CloseAudio(); 

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