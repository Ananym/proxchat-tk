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
                        uint ssrc = rtpPacket.Header.SyncSource;
                        string? foundPeerId = null;

                        // Find peerId by SSRC
                        foreach (var entry in _peerConnections)
                        {
                            if (entry.Value.RemoteAudioSsrc == ssrc)
                            {
                                foundPeerId = entry.Key;
                                break;
                            }
                        }

                        if (foundPeerId != null)
                        {
                            bool hasAudio = false;
                            if (rtpPacket.Payload.Length > 0)
                            {
                                hasAudio = rtpPacket.Payload.Any(b => b != 0xFF);
                            }

                            if (_random.NextDouble() < LOG_PROBABILITY) // Probabilistic logging
                            {
                                _debugLog.LogWebRtc($"(Sampled Log) RTP for SSRC {ssrc} (Peer {foundPeerId}): seq={rtpPacket.Header.SequenceNumber}, pt={rtpPacket.Header.PayloadType}, size={rtpPacket.Payload.Length}, hasAudio={hasAudio}");
                            }

                            // Call AudioService to play the audio
                            _audioService.PlayAudio(foundPeerId, rtpPacket.Payload, rtpPacket.Payload.Length);
                        }
                        else
                        {
                            if (_random.NextDouble() < LOG_PROBABILITY) // Log if SSRC is unknown only occasionally
                            {
                                _debugLog.LogWebRtc($"[WARNING] Received audio RTP packet with SSRC {ssrc} but no associated peer found.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // The original peerId in this log was from the closure of CreatePeerConnection, which isn't correct for this context.
                        // Logging general error now.
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
                else if (connState == RTCPeerConnectionState.failed || connState == RTCPeerConnectionState.closed)
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

            // --- Data Channel ---
            // Only initiator explicitly creates the data channel.
            // The other peer will receive it via the ondatachannel event.
            if (isInitiator)
            {
                var dataChannel = await pc.createDataChannel("position").ConfigureAwait(false);
                if (dataChannel == null)
                {
                    _debugLog.LogWebRtc($"Failed to create data channel for {peerId} as initiator");
                    RemovePeerConnection(peerId);
                    return;
                }
                ConfigureDataChannel(peerId, dataChannel);
                _debugLog.LogWebRtc($"Initiator {peerId} created data channel 'position'.");
            }
            
            // All peers listen for incoming data channels.
            // For the initiator, this would be for channels initiated by the remote peer (if any, not typical for this app's design).
            // For the non-initiator, this is how they receive the channel created by the initiator.
            pc.ondatachannel += (dc) =>
            {
                _debugLog.LogWebRtc($"Received data channel '{dc.label}' from peer {peerId}. Configuring...");
                ConfigureDataChannel(peerId, dc);
            };

            // --- Offer Creation (if initiator) ---
            if (isInitiator)
            {
                try
                {
                    var offerTask = Task.Run(() => pc.createOffer());
                    var offer = await offerTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    
                    _debugLog.LogWebRtc($"Created offer for {peerId} with SDP: {offer.sdp}");
                    
                    var result = pc.setRemoteDescription(offer);
                    if (result != SetDescriptionResultEnum.OK)
                    {
                        _debugLog.LogWebRtc($"Failed to set remote description for {peerId}: {result}");
                        RemovePeerConnection(peerId);
                        return;
                    }
                    else
                    {
                        // After setting remote description (the offer), the remote track from offerer should be known.
                        if (pc.AudioRemoteTrack?.Ssrc != 0 && pc.AudioRemoteTrack?.Ssrc != null)
                        {
                            state.RemoteAudioSsrc = pc.AudioRemoteTrack.Ssrc; // SSRC of the track being sent by the offerer
                            _debugLog.LogWebRtc($"Associated remote audio SSRC {state.RemoteAudioSsrc} with offering peer {peerId}.");
                        }
                        else
                        {
                            _debugLog.LogWebRtc($"[WARNING] Could not get remote audio SSRC for offering peer {peerId} from AudioRemoteTrack after processing offer (SSRC: {pc.AudioRemoteTrack?.Ssrc}).");
                        }
                    }

                    var answerTask = Task.Run(() => pc.createAnswer());
                    var answer = answerTask.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    _debugLog.LogWebRtc($"Created answer for {peerId} with SDP: {answer.sdp}");

                    Task.Run(async () => await pc.setLocalDescription(answer)).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    _signalingService.SendAnswerAsync(peerId, answer.toJSON()).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _debugLog.LogWebRtc($"Error creating/sending offer for {peerId}: {ex.Message}");
                    RemovePeerConnection(peerId);
                }
            }
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
            
            dc.onmessage += (channel, type, data) =>
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (data == null) return;
                        string message = Encoding.UTF8.GetString(data);
                        var posData = JsonConvert.DeserializeObject<PositionData>(message);
                        if (posData == null) return;

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
            
            dc.onopen += () => {
                DataChannelOpened?.Invoke(this, peerId);
            };
        }
    }

    private void OnOfferReceived(object? sender, OfferPayload payload)
    {
        if (payload.SenderId == null || payload.Offer == null) return;

        _ = Task.Run(() =>
        {
            var peerId = payload.SenderId;
            RTCPeerConnection pc;
            if (!_peerConnections.TryGetValue(peerId, out var state))
            {
                CreatePeerConnection(peerId, isInitiator: false).GetAwaiter().GetResult();
                if (!_peerConnections.TryGetValue(peerId, out state))
                {
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
                
                var result = pc.setRemoteDescription(offerSdp);
                if (result != SetDescriptionResultEnum.OK)
                {
                    _debugLog.LogWebRtc($"Failed to set remote description for {peerId}: {result}");
                    RemovePeerConnection(peerId);
                    return;
                }
                else
                {
                    // After setting remote description (the offer), the remote track from offerer should be known.
                    if (pc.AudioRemoteTrack?.Ssrc != 0 && pc.AudioRemoteTrack?.Ssrc != null)
                    {
                        state.RemoteAudioSsrc = pc.AudioRemoteTrack.Ssrc; // SSRC of the track being sent by the offerer
                        _debugLog.LogWebRtc($"Associated remote audio SSRC {state.RemoteAudioSsrc} with offering peer {peerId}.");
                    }
                    else
                    {
                        _debugLog.LogWebRtc($"[WARNING] Could not get remote audio SSRC for offering peer {peerId} from AudioRemoteTrack after processing offer (SSRC: {pc.AudioRemoteTrack?.Ssrc}).");
                    }
                }

                var answerTask = Task.Run(() => pc.createAnswer());
                var answer = answerTask.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                _debugLog.LogWebRtc($"Created answer for {peerId} with SDP: {answer.sdp}");

                Task.Run(async () => await pc.setLocalDescription(answer)).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _signalingService.SendAnswerAsync(peerId, answer.toJSON()).GetAwaiter().GetResult();
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

        _ = Task.Run(() =>
        {
            var peerId = payload.SenderId;
            if (_peerConnections.TryGetValue(peerId, out var state))
            {
                try
                {
                    var answerSdp = JsonConvert.DeserializeObject<RTCSessionDescriptionInit>(payload.Answer); 
                    if (answerSdp == null) return;
                    
                    _debugLog.LogWebRtc($"Received answer from {peerId} with SDP: {answerSdp.sdp}");
                    
                    var result = state.PeerConnection.setRemoteDescription(answerSdp);
                    if (result != SetDescriptionResultEnum.OK)
                    { 
                        _debugLog.LogWebRtc($"Failed to set remote description for {peerId}: {result}");
                        RemovePeerConnection(peerId); 
                    }
                    else
                    {
                        // After offerer sets remote description (the answer), the remote track from answerer is known.
                        // This SSRC is for audio coming FROM the peer who sent the ANSWER.
                        if (state.PeerConnection.AudioRemoteTrack?.Ssrc != 0 && state.PeerConnection.AudioRemoteTrack?.Ssrc != null)
                        {
                            // This assumes the 'state' object here is for the connection with the peer who just answered.
                            // If we want to store the SSRC of the answering peer's audio track that *we* will receive,
                            // this is the correct place.
                            state.RemoteAudioSsrc = state.PeerConnection.AudioRemoteTrack.Ssrc; 
                            _debugLog.LogWebRtc($"Associated remote audio SSRC {state.RemoteAudioSsrc} with answering peer {peerId}.");
                        }
                        else
                        {
                             _debugLog.LogWebRtc($"[WARNING] Could not get remote audio SSRC for answering peer {peerId} from AudioRemoteTrack after processing answer (SSRC: {state.PeerConnection.AudioRemoteTrack?.Ssrc}).");
                        }
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
        if (_peerConnections.TryGetValue(peerId, out var state) && state.DataChannel?.readyState == RTCDataChannelState.open)
        {
            try
            {
                var positionData = new PositionData { MapId = mapId, X = x, Y = y, CharacterName = characterName };
                string message = JsonConvert.SerializeObject(positionData);
                state.DataChannel.send(Encoding.UTF8.GetBytes(message));
                _debugLog.LogWebRtc($"Sent position update to {peerId}: MapId={mapId}, X={x}, Y={y}");
            }
            catch (Exception ex)
            {
                _debugLog.LogWebRtc($"Error sending position to {peerId}: {ex.Message}");
            }
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
    public uint RemoteAudioSsrc { get; set; } // To map received RTP packets to a peer
    // Add other relevant state if needed, e.g., audio stream references if managed separately
} 