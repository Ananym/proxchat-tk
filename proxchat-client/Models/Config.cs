using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace ProxChatClient.Models;

public class PeerPersistentState
{
    public float Volume { get; set; } = 0.5f;
    public bool IsMuted { get; set; } = false;
}

public class Config
{
    public WebSocketServerConfig WebSocketServer { get; set; } = new();
    public int Channel { get; set; } = 0;
    public string GameDataIpcChannel { get; set; } = "game-data-channel";
    public AudioConfig AudioSettings { get; set; } = new();
    public Dictionary<string, PeerPersistentState> PeerSettings { get; set; } = new Dictionary<string, PeerPersistentState>();
}

public class WebSocketServerConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 443;
}

public class AudioConfig
{
    public float VolumeScale { get; set; } = 0.5f;
    public float InputVolumeScale { get; set; } = 1.0f;
    public float MinBroadcastThreshold { get; set; } = 0.0f;
    public string? SelectedInputDevice { get; set; }
    public bool IsPushToTalk { get; set; } = false;
    public string PushToTalkKey { get; set; } = "F12";
    public string MuteSelfKey { get; set; } = "F11";
} 