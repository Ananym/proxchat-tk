// this file describes the communication with the signalling server used to orchestrate connection initation.
// it does not describe the communication with the peers themselves.

using Newtonsoft.Json;
using ProxChatClient.Models;
using System.Reactive.Linq;
using Websocket.Client;
using ProxChatClient.Models.Signaling;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Text.Json;

namespace ProxChatClient.Services;

public class SignalingService : IDisposable
{
    private readonly Uri _serverUri;
    private string _clientId;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;
    private readonly DebugLogService _debugLog;
    
    // throttle position update logging
    private int _lastLoggedMapId = -1;
    private DateTime _lastPositionLogTime = DateTime.MinValue;
    private int _messagesSent = 0;

    public event EventHandler<List<string>>? NearbyClientsReceived;
    public event EventHandler<OfferPayload>? OfferReceived;
    public event EventHandler<AnswerPayload>? AnswerReceived;
    public event EventHandler<IceCandidatePayload>? IceCandidateReceived;
    public event EventHandler<string>? SignalingErrorReceived;
    public event EventHandler<bool>? ConnectionStatusChanged;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public string ClientId => _clientId;

    public SignalingService(WebSocketServerConfig config, DebugLogService debugLog)
    {
        // trim trailing slash from host
        var host = config.Host.TrimEnd('/');
        
        // use wss:// for secure connections (port 443) or ws:// for insecure (other ports)
        var protocol = config.Port == 443 ? "wss" : "ws";
        
        // for standard ports (443 for wss, 80 for ws), omit port from URI
        var portSuffix = (protocol == "wss" && config.Port == 443) || (protocol == "ws" && config.Port == 80) 
            ? "" 
            : $":{config.Port}";
            
        _serverUri = new Uri($"{protocol}://{host}{portSuffix}");
        _clientId = Guid.NewGuid().ToString();
        _debugLog = debugLog;
    }

    public async Task Connect()
    {
        if (_webSocket != null)
        {
            _debugLog.LogSignaling("WebSocket already connected.");
            return;
        }

        try
        {
            _debugLog.LogSignaling($"Attempting to connect to signaling server at {_serverUri}");
            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(_serverUri, CancellationToken.None);
            _debugLog.LogSignaling("Connected to signaling server.");
            ConnectionStatusChanged?.Invoke(this, true);

            // Start receiving messages
            _receiveCts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveMessages(_receiveCts.Token));
        }
        catch (WebSocketException ex)
        {
            _debugLog.LogSignaling($"WebSocket connection failed: {ex.Message}");
            await HandleDisconnect();
            SignalingErrorReceived?.Invoke(this, $"Failed to connect to signaling server: {ex.Message}");
            throw new SignalingConnectionException("Failed to connect to signaling server", ex);
        }
        catch (Exception ex)
        {
            _debugLog.LogSignaling($"Unexpected error during connection: {ex.Message}");
            await HandleDisconnect();
            SignalingErrorReceived?.Invoke(this, $"Unexpected error during connection: {ex.Message}");
            throw new SignalingConnectionException("Unexpected error during connection", ex);
        }
    }

    private async Task ReceiveMessages(CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (_webSocket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _debugLog.LogSignaling("Received close message from server.");
                    await HandleDisconnect();
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                HandleMessage(message);
            }
        }
        catch (OperationCanceledException)
        {
            _debugLog.LogSignaling("Message receive operation was canceled.");
        }
        catch (WebSocketException ex)
        {
            _debugLog.LogSignaling($"WebSocket error during message receive: {ex.Message}");
            await HandleDisconnect();
        }
        catch (Exception ex)
        {
            _debugLog.LogSignaling($"Unexpected error during message receive: {ex.Message}");
            await HandleDisconnect();
        }
    }

    private async Task HandleDisconnect()
    {
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _receiveCts = null;

        if (_webSocket != null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _debugLog.LogSignaling($"Error during WebSocket close: {ex.Message}");
            }
            finally
            {
                _webSocket.Dispose();
                _webSocket = null;
            }
        }
        ConnectionStatusChanged?.Invoke(this, false);
    }

    public async Task Disconnect()
    {
        await HandleDisconnect();
    }

    private void HandleMessage(string messageText)
    {
        try
        {
            var baseMessage = JsonConvert.DeserializeObject<ServerMessageBase>(messageText);
            if (baseMessage == null) return;

            switch (baseMessage.Type)
            {
                case ServerMessageType.NearbyPeers:
                    var nearbyPeersData = JsonConvert.DeserializeObject<NearbyPeersData>(messageText);
                    if (nearbyPeersData?.Peers != null) 
                    {
                        _debugLog.LogSignaling($"Received nearby peers: {string.Join(", ", nearbyPeersData.Peers)}");
                        
                        // check if our own client ID is in the list
                        if (nearbyPeersData.Peers.Contains(_clientId))
                        {
                            _debugLog.LogSignaling($"[BUG] Server sent our own client ID {_clientId} in nearby peers list!");
                            
                            // remove our own ID from the list to prevent self-connection
                            nearbyPeersData.Peers = nearbyPeersData.Peers.Where(id => id != _clientId).ToList();
                            _debugLog.LogSignaling($"[FIX] Removed own client ID from nearby list. Remaining peers: {string.Join(", ", nearbyPeersData.Peers)}");
                        }
                        
                        NearbyClientsReceived?.Invoke(this, nearbyPeersData.Peers);
                    }
                    break;

                case ServerMessageType.ReceiveOffer:
                    var offerData = JsonConvert.DeserializeObject<ReceiveOfferData>(messageText);
                    if (offerData?.Data != null) 
                    {
                        _debugLog.LogSignaling($"Received offer from {offerData.Data.SenderId}");
                        OfferReceived?.Invoke(this, offerData.Data);
                    }
                    break;

                case ServerMessageType.ReceiveAnswer:
                    var answerData = JsonConvert.DeserializeObject<ReceiveAnswerData>(messageText);
                    if (answerData?.Data != null) 
                    {
                        _debugLog.LogSignaling($"Received answer from {answerData.Data.SenderId}");
                        AnswerReceived?.Invoke(this, answerData.Data);
                    }
                    break;

                case ServerMessageType.ReceiveIceCandidate:
                    var candidateData = JsonConvert.DeserializeObject<ReceiveIceCandidateData>(messageText);
                    if (candidateData?.Data != null) 
                    {
                        _debugLog.LogSignaling($"Received ICE candidate from {candidateData.Data.SenderId}");
                        IceCandidateReceived?.Invoke(this, candidateData.Data);
                    }
                    break;

                case ServerMessageType.Error:
                    var errorData = JsonConvert.DeserializeObject<ErrorData>(messageText);
                    if (errorData?.Message != null) 
                    {
                        _debugLog.LogSignaling($"Signaling Error Received from Server: {errorData.Message}");
                        SignalingErrorReceived?.Invoke(this, errorData.Message);
                    }
                    break;

                default:
                    _debugLog.LogSignaling($"Received unknown message type: {baseMessage.Type}");
                    break;
            }
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            _debugLog.LogSignaling($"Failed to parse incoming signaling message: {ex.Message}, Raw: {messageText}");
        }
        catch (Exception ex)
        {
            _debugLog.LogSignaling($"Error handling incoming signaling message: {ex.Message}");
        }
    }

    public async Task UpdatePosition(int mapId, int x, int y, int channel, int gameId)
    {
        if (!IsConnected) return;

        var positionData = new UpdatePositionData
        {
            ClientId = _clientId,
            MapId = mapId,
            X = x,
            Y = y,
            Channel = channel,
            GameId = gameId
        };
        
        // only log position updates occasionally to avoid spam
        if (mapId != _lastLoggedMapId || DateTime.Now - _lastPositionLogTime > TimeSpan.FromSeconds(5))
        {
            _debugLog.LogSignaling($"Sending position: ClientId={_clientId}, MapId={mapId}, X={x}, Y={y}, Channel={channel}, GameId={gameId}");
            _lastLoggedMapId = mapId;
            _lastPositionLogTime = DateTime.Now;
        }
        
        var message = ClientMessage.CreateUpdatePosition(positionData);
        await SendMessageAsync(message);
    }

    public async Task SendOfferAsync(string targetId, string offerSdp)
    {
        _debugLog.LogSignaling($"Sending offer to {targetId}");
        var message = ClientMessage.CreateSendOffer(targetId, offerSdp);
        await SendMessageAsync(message);
    }

    public async Task SendAnswerAsync(string targetId, string answerSdp)
    {
        _debugLog.LogSignaling($"Sending answer to {targetId}");
        var message = ClientMessage.CreateSendAnswer(targetId, answerSdp);
        await SendMessageAsync(message);
    }

    public async Task SendIceCandidateAsync(string targetId, string candidateJson)
    {
        _debugLog.LogSignaling($"Sending ICE candidate to {targetId}");
        var message = ClientMessage.CreateSendIceCandidate(targetId, candidateJson);
        await SendMessageAsync(message);
    }

    public async Task RequestPeerRefresh()
    {
        _debugLog.LogSignaling("Requesting peer refresh from server");
        var message = ClientMessage.CreateRequestPeerRefresh();
        await SendMessageAsync(message);
    }

    private async Task SendMessageAsync(ClientMessage message)
    {
        if (_webSocket?.State != WebSocketState.Open)
        {
            _debugLog.LogSignaling($"Attempted to send signaling message while disconnected. Type: {message.Type}");
            return;
        }

        try
        {
            var json = JsonConvert.SerializeObject(message);
            
            // log first few messages for debugging
            if (_messagesSent <= 3)
            {
                _debugLog.LogSignaling($"Sending JSON message #{_messagesSent + 1}: {json}");
            }
            _messagesSent++;
            
            var bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            
            // removed excessive "sent message" logging - only log errors now
        }
        catch (Exception ex)
        {
            _debugLog.LogSignaling($"Failed to send signaling message (Type: {message.Type}): {ex.Message}");
        }
    }

    public void Dispose()
    {
        _ = HandleDisconnect();
    }

    // add method to regenerate client id and reconnect
    public async Task RegenerateClientIdAndReconnect()
    {
        _debugLog.LogSignaling("Regenerating client ID for anonymity and reconnecting...");
        
        // disconnect first if connected
        if (IsConnected)
        {
            await HandleDisconnect();
        }
        
        // generate new client id
        _clientId = Guid.NewGuid().ToString();
        _debugLog.LogSignaling($"Generated new client ID: {_clientId}");
        
        // reconnect with new id
        await Connect();
    }
} 