using Newtonsoft.Json;
using System.Collections.Generic;

namespace ProxChatClient.Models.Signaling;

// Base class for discriminated union
public abstract class SignalingMessageBase
{
    [JsonProperty("type")]
    public string? Type { get; set; }
}

//-----------------------------------------------
// Client -> Server Messages (Serialization)
//-----------------------------------------------

public class UpdatePositionData
{
    [JsonProperty("client_id")]
    public string? ClientId { get; set; }
    [JsonProperty("map_id")]
    public int MapId { get; set; }
    [JsonProperty("x")]
    public int X { get; set; }
    [JsonProperty("y")]
    public int Y { get; set; }
}

public class ClientMessage : SignalingMessageBase
{
    [JsonProperty("data")]
    public object? Data { get; set; }

    // Static factory methods for creating messages
    public static ClientMessage CreateUpdatePosition(UpdatePositionData data) =>
        new ClientMessage { Type = "UpdatePosition", Data = data };

    public static ClientMessage CreateSendOffer(string targetId, string offer) =>
        new ClientMessage { Type = "SendOffer", Data = new { target_id = targetId, offer } };

    public static ClientMessage CreateSendAnswer(string targetId, string answer) =>
        new ClientMessage { Type = "SendAnswer", Data = new { target_id = targetId, answer } };

    public static ClientMessage CreateSendIceCandidate(string targetId, string candidate) =>
        new ClientMessage { Type = "SendIceCandidate", Data = new { target_id = targetId, candidate } };

    public static ClientMessage CreateDisconnect() =>
        new ClientMessage { Type = "Disconnect", Data = null }; // No data needed
}

//-----------------------------------------------
// Server -> Client Messages (Deserialization)
//-----------------------------------------------

// Base class for incoming messages, allows checking 'Type' before deserializing 'Data'
public class ServerMessageBase : SignalingMessageBase
{
    // We'll deserialize 'Data' separately based on 'Type'
}

// Specific data payloads for Server -> Client messages

public class NearbyPeersData
{
    [JsonProperty("data")]
    public List<string>? Peers { get; set; }
}

public class ReceiveOfferData
{
    [JsonProperty("data")]
    public OfferPayload? Data { get; set; }
}
public class OfferPayload
{
    [JsonProperty("sender_id")]
    public string? SenderId { get; set; }
    [JsonProperty("offer")]
    public string? Offer { get; set; }
}

public class ReceiveAnswerData
{
    [JsonProperty("data")]
    public AnswerPayload? Data { get; set; }
}
public class AnswerPayload
{
    [JsonProperty("sender_id")]
    public string? SenderId { get; set; }
    [JsonProperty("answer")]
    public string? Answer { get; set; }
}

public class ReceiveIceCandidateData
{
    [JsonProperty("data")]
    public IceCandidatePayload? Data { get; set; }
}
public class IceCandidatePayload
{
    [JsonProperty("sender_id")]
    public string? SenderId { get; set; }
    [JsonProperty("candidate")]
    public string? Candidate { get; set; }
}

public class ErrorData
{
    [JsonProperty("data")]
    public string? Message { get; set; }
}

// Constants for message types
public static class ServerMessageType
{
    public const string NearbyPeers = "NearbyPeers";
    public const string ReceiveOffer = "ReceiveOffer";
    public const string ReceiveAnswer = "ReceiveAnswer";
    public const string ReceiveIceCandidate = "ReceiveIceCandidate";
    public const string Error = "Error";
} 