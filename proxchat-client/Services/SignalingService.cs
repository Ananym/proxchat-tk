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

namespace ProxChatClient.Services;

public class SignalingService : IDisposable
{
    private readonly Uri _serverUri;
    private readonly string _clientId;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveCts;

    public event EventHandler<List<string>>? NearbyClientsReceived;
    public event EventHandler<OfferPayload>? OfferReceived;
    public event EventHandler<AnswerPayload>? AnswerReceived;
    public event EventHandler<IceCandidatePayload>? IceCandidateReceived;
    public event EventHandler<string>? SignalingErrorReceived;
    public event EventHandler<bool>? ConnectionStatusChanged;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public string ClientId => _clientId;

    public SignalingService(WebSocketServerConfig config)
    {
        _serverUri = new Uri($"ws://{config.Host}:{config.Port}");
        _clientId = Guid.NewGuid().ToString();
    }

    public async Task Connect()
    {
        if (_webSocket != null)
        {
            Debug.WriteLine("WebSocket already connected.");
            return;
        }

        try
        {
            Debug.WriteLine($"Attempting to connect to signaling server at {_serverUri}");
            _webSocket = new ClientWebSocket();
            await _webSocket.ConnectAsync(_serverUri, CancellationToken.None);
            Debug.WriteLine("Connected to signaling server.");
            ConnectionStatusChanged?.Invoke(this, true);

            // Start receiving messages
            _receiveCts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveMessages(_receiveCts.Token));
        }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"WebSocket connection failed: {ex.Message}");
            await HandleDisconnect();
            SignalingErrorReceived?.Invoke(this, $"Failed to connect to signaling server: {ex.Message}");
            throw new SignalingConnectionException("Failed to connect to signaling server", ex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unexpected error during connection: {ex.Message}");
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
                    Debug.WriteLine("Received close message from server.");
                    await HandleDisconnect();
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Debug.WriteLine($"Received message: {message}");
                HandleMessage(message);
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("Message receive operation was canceled.");
        }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"WebSocket error during message receive: {ex.Message}");
            await HandleDisconnect();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unexpected error during message receive: {ex.Message}");
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
                Debug.WriteLine($"Error during WebSocket close: {ex.Message}");
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
                    if (nearbyPeersData?.Peers != null) NearbyClientsReceived?.Invoke(this, nearbyPeersData.Peers);
                    break;

                case ServerMessageType.ReceiveOffer:
                    var offerData = JsonConvert.DeserializeObject<ReceiveOfferData>(messageText);
                    if (offerData?.Data != null) OfferReceived?.Invoke(this, offerData.Data);
                    break;

                case ServerMessageType.ReceiveAnswer:
                    var answerData = JsonConvert.DeserializeObject<ReceiveAnswerData>(messageText);
                    if (answerData?.Data != null) AnswerReceived?.Invoke(this, answerData.Data);
                    break;

                case ServerMessageType.ReceiveIceCandidate:
                    var candidateData = JsonConvert.DeserializeObject<ReceiveIceCandidateData>(messageText);
                    if (candidateData?.Data != null) IceCandidateReceived?.Invoke(this, candidateData.Data);
                    break;

                case ServerMessageType.Error:
                    var errorData = JsonConvert.DeserializeObject<ErrorData>(messageText);
                    if (errorData?.Message != null) 
                    {
                        Debug.WriteLine($"Signaling Error Received from Server: {errorData.Message}");
                        SignalingErrorReceived?.Invoke(this, errorData.Message);
                    }
                    break;

                default:
                    Debug.WriteLine($"Received unknown message type: {baseMessage.Type}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Failed to parse incoming signaling message: {ex.Message}, Raw: {messageText}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error handling incoming signaling message: {ex.Message}");
        }
    }

    public async Task UpdatePosition(int mapId, int x, int y)
    {
        if (!IsConnected) return;

        var positionData = new UpdatePositionData
        {
            ClientId = _clientId,
            MapId = mapId,
            X = x,
            Y = y
        };
        var message = ClientMessage.CreateUpdatePosition(positionData);
        await SendMessageAsync(message);
    }

    public async Task SendOfferAsync(string targetId, string offerSdp)
    {
        var message = ClientMessage.CreateSendOffer(targetId, offerSdp);
        await SendMessageAsync(message);
    }

    public async Task SendAnswerAsync(string targetId, string answerSdp)
    {
        var message = ClientMessage.CreateSendAnswer(targetId, answerSdp);
        await SendMessageAsync(message);
    }

    public async Task SendIceCandidateAsync(string targetId, string candidateJson)
    {
        var message = ClientMessage.CreateSendIceCandidate(targetId, candidateJson);
        await SendMessageAsync(message);
    }

    private async Task SendMessageAsync(ClientMessage message)
    {
        if (!IsConnected) 
        {
            Debug.WriteLine($"Attempted to send signaling message while disconnected. Type: {message.Type}");
            return;
        }

        try
        {
            var jsonMessage = JsonConvert.SerializeObject(message);
            var bytes = Encoding.UTF8.GetBytes(jsonMessage);
            await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to send signaling message (Type: {message.Type}): {ex.Message}");
            await HandleDisconnect();
        }
    }

    public void Dispose()
    {
        Debug.WriteLine("Disposing SignalingService...");
        _ = Disconnect();
    }
} 