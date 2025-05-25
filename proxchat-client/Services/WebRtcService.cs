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

namespace ProxChatClient.Services;

public class WebRtcService : IDisposable
{
    private readonly ConcurrentDictionary<string, PeerConnectionState> _peerConnections = new();
    private readonly SignalingService _signalingService;
    private readonly AudioService _audioService;
    private readonly float _maxDistance;
    private readonly DebugLogService _debugLog;
    private bool _isInitialized;
    private uint _rtpTimestamp = 0; // Add timestamp tracking

    private static readonly Random _random = new Random(); // For probabilistic logging
    private const double LOG_PROBABILITY = 0.01; // 1% chance to log detailed packet info

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

    public WebRtcService(AudioService audioService, SignalingService signalingService, float maxDistance, DebugLogService debugLog)
    {
        _audioService = audioService;
        _signalingService = signalingService;
        _maxDistance = maxDistance;
        _debugLog = debugLog;
        _isInitialized = false;

        // Initialize audio system first
        InitializeAudioSystem();

        // Subscribe to audio data events
        _audioService.EncodedAudioPacketAvailable += OnEncodedAudioPacketReadyToSend;

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
            if (_audioEndPoint != null)
            {
                _audioEndPoint.CloseAudio();
                _audioEndPoint = null;
            }

            // Create audio endpoint with MuLaw encoder
            _audioEndPoint = new WindowsAudioEndPoint(new AudioEncoder());
            _debugLog.LogWebRtc("Created WindowsAudioEndPoint with MuLaw encoder");
            
            // Create media track with MuLaw format (PCMU)
            var audioFormat = new AudioFormat(AudioCodecsEnum.PCMU, 0);
            _audioTrack = new MediaStreamTrack(audioFormat, MediaStreamStatusEnum.SendRecv);
            _debugLog.LogWebRtc($"Created audio track with format: {audioFormat.Codec}, status: {MediaStreamStatusEnum.SendRecv}");

            // Start audio
            _audioEndPoint.StartAudio();
            _debugLog.LogWebRtc("Started audio endpoint");
        }
        catch (Exception ex)
        {
            _debugLog.LogWebRtc($"Error initializing audio system: {ex.Message}");
            
            // Clean up any partially initialized resources
            if (_audioEndPoint != null)
            {
                try
                {
                    _audioEndPoint.CloseAudio();
                }
                catch { }
                _audioEndPoint = null;
            }
            _audioTrack = null;
            
            throw;
        }
    }

    public async Task CreatePeerConnection(string peerId, bool isInitiator)
    {
        EnsureInitialized();
        
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
                                hasAudio = rtpPacket.Payload.Any(b => b != 0xFF); // MuLaw silence is 0xFF
                            }

                            if (_random.NextDouble() < LOG_PROBABILITY) // Probabilistic logging
                            {
                                _debugLog.LogWebRtc($"(Sampled Log) RTP for peer {foundPeerId} (Identified by PC instance, incoming packet SSRC: {incomingSsrc}): seq={rtpPacket.Header.SequenceNumber}, pt={rtpPacket.Header.PayloadType}, size={rtpPacket.Payload.Length}, hasAudio={hasAudio}");
                            }

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

        _ = Task.Run(async () => // Ensure async Task
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
                string message = JsonConvert.SerializeObject(positionData);
                // TEMP LOG: Sending position update
                _debugLog.LogWebRtc($"[TEMP] DC Sending Position to {peerId}: {message}");
                state.DataChannel.send(Encoding.UTF8.GetBytes(message));
                _debugLog.LogWebRtc($"Sent position update to {peerId}: MapId={mapId}, X={x}, Y={y}");
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
        _debugLog.LogWebRtc($"Sending position update to all peers: MapId={mapId}, X={x}, Y={y}, Name={characterName}");
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

    private void OnEncodedAudioPacketReadyToSend(object? sender, EncodedAudioPacketEventArgs e)
    {
        // if (_audioEndPoint == null) return; // _audioEndPoint is not directly used for sending via track

        try
        {
            byte[] audioData = e.Buffer;
            int samplesInPacket = e.Samples; // Changed from BytesRecorded to Samples
            
            // Check if audio data contains non-silence
            bool hasAudio = false;
            if (audioData.Length > 0)
            {
                // For MuLaw (PCMU), 0xFF is silence, so check if any byte is different
                hasAudio = audioData.Any(b => b != 0xFF);
            }

            // _debugLog.LogWebRtc($"OnEncodedAudioPacketReadyToSend: samples={samplesInPacket}, hasAudio={hasAudio}. Attempting to send via MediaStreamTrack.");
            if (_random.NextDouble() < LOG_PROBABILITY) // Probabilistic logging for the general send event
            {
                _debugLog.LogWebRtc($"(Sampled Log) OnEncodedAudioPacketReadyToSend: samples={samplesInPacket}, hasAudio={hasAudio}. Attempting to send to all peers.");
            }

            foreach (var peerId in _peerConnections.Keys)
            {
                if (_peerConnections.TryGetValue(peerId, out var state))
                {
                    try
                    {
                        // Check if we have a valid peer connection and audio track
                        if (state.PeerConnection.connectionState != RTCPeerConnectionState.connected)
                        {
                            _debugLog.LogWebRtc($"Peer connection not connected for {peerId}, skipping audio send");
                            continue;
                        }
                        if (_audioTrack == null)
                        {
                            _debugLog.LogWebRtc($"Audio track is null for peer {peerId}, skipping audio send");
                            continue;
                        }

                        // Send the audio data using the MediaStreamTrack.
                        // Assumes audioData from AudioService is PCMU encoded.
                        // _rtpTimestamp is the starting timestamp for this packet.
                        // The duration is implicit by the timestamp increments and the packet content (samplesInPacket).
                        // _audioTrack.SendAudioFrame(_rtpTimestamp, audioData); // Changed from SendEncodedAudio(ts, duration, data)

                        // Corrected: Use the PeerConnection to send audio. 
                        // The duration (samplesInPacket) is how much the RTP timestamp will be incremented by the sender for this packet.
                        state.PeerConnection.SendAudio((uint)samplesInPacket, audioData);

                        if (hasAudio)
                        {
                            if (_random.NextDouble() < LOG_PROBABILITY) // Probabilistic logging for sent packets
                            {
                                _debugLog.LogWebRtc($"(Sampled Log) Sent audio packet via PeerConnection.SendAudio to {peerId}: duration={samplesInPacket}, size={audioData.Length}, local_ts_approx={_rtpTimestamp}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _debugLog.LogWebRtc($"Error sending audio data to {peerId} via track: {ex.Message}");
                    }
                }
            }

            // Increment RTP timestamp by the number of samples in the packet.
            // For PCMU (8000Hz), samplesInPacket represents the sample count for this packet.
            _rtpTimestamp += (uint)samplesInPacket; 
        }
        catch (Exception ex)
        {
            _debugLog.LogWebRtc($"Error in OnEncodedAudioPacketReadyToSend: {ex.Message}");
        }
    }

    public void Dispose()
    {
        CloseAllConnections();

        _signalingService.OfferReceived -= OnOfferReceived;
        _signalingService.AnswerReceived -= OnAnswerReceived;
        _signalingService.IceCandidateReceived -= OnIceCandidateReceived;
        _audioService.EncodedAudioPacketAvailable -= OnEncodedAudioPacketReadyToSend;
        
        _audioEndPoint?.CloseAudio(); 

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