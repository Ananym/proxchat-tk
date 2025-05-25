# ProxChat Client Configuration Guide

This guide explains how to configure the ProxChat client.

## Quick Setup

1. **Copy the default config:**

   ```
   copy config.json.default config.json
   ```

2. **Edit `config.json`** with your settings (see sections below)

3. **Run the client** - it will automatically load your configuration

## Configuration File Structure

### Server Connection

```json
{
  "WebSocketServer": {
    "Host": "127.0.0.1",
    "Port": 8080
  }
}
```

- **Host**: IP address or hostname of the signaling server
- **Port**: Port number the server is listening on

**Examples:**

- Local server: `"Host": "127.0.0.1", "Port": 8080`
- Remote server: `"Host": "your-server.com", "Port": 8080`
- Custom port: `"Host": "127.0.0.1", "Port": 9999`

### Channel Settings

```json
{
  "Channel": 0
}
```

- **Channel**: Voice chat channel number (integer)
- Only players on the same channel can hear each other
- Default: `0`

**Examples:**

- Default channel: `"Channel": 0`
- Private group: `"Channel": 1`
- Team A: `"Channel": 100`
- Team B: `"Channel": 101`

### Audio Settings

```json
{
  "AudioSettings": {
    "VolumeScale": 0.5,
    "InputVolumeScale": 1.0,
    "MinBroadcastThreshold": 0.0,
    "SelectedInputDevice": null,
    "IsPushToTalk": false,
    "PushToTalkKey": "F12",
    "MuteSelfKey": "F11"
  }
}
```

#### Volume Controls

- **VolumeScale**: Master volume for all incoming audio (0.0 to 1.0)
- **InputVolumeScale**: Microphone input volume (0.0 to 2.0+)
- **MinBroadcastThreshold**: Minimum volume to trigger voice transmission (0.0 to 1.0)

#### Device Selection

- **SelectedInputDevice**: Specific microphone to use
  - `null` = use system default
  - `"Microphone (Specific Device Name)"` = use named device

#### Push-to-Talk

- **IsPushToTalk**: Enable push-to-talk mode

  - `false` = voice activation (always listening)
  - `true` = only transmit when key is pressed

- **PushToTalkKey**: Key to hold for push-to-talk
- **MuteSelfKey**: Key to toggle microphone mute

**Available Keys:** `F1`-`F12`, `Space`, `Tab`, `Ctrl`, `Alt`, `Shift`, etc.

### Per-Player Settings

```json
{
  "PeerSettings": {
    "player-guid-123": {
      "Volume": 0.8,
      "IsMuted": false
    },
    "annoying-player-456": {
      "Volume": 0.1,
      "IsMuted": true
    }
  }
}
```

- These settings are automatically saved by the client
- **Volume**: Individual player volume (0.0 to 2.0+)
- **IsMuted**: Whether this specific player is muted

## Example Configurations

### Local Development

```json
{
  "WebSocketServer": {
    "Host": "127.0.0.1",
    "Port": 8080
  },
  "Channel": 0,
  "AudioSettings": {
    "VolumeScale": 0.7,
    "InputVolumeScale": 1.0,
    "IsPushToTalk": false
  }
}
```

### Production Server

```json
{
  "WebSocketServer": {
    "Host": "your-game-server.com",
    "Port": 8080
  },
  "Channel": 1,
  "AudioSettings": {
    "VolumeScale": 0.5,
    "InputVolumeScale": 1.2,
    "IsPushToTalk": true,
    "PushToTalkKey": "V"
  }
}
```

### Quiet Environment (Sensitive Microphone)

```json
{
  "AudioSettings": {
    "VolumeScale": 0.6,
    "InputVolumeScale": 0.7,
    "MinBroadcastThreshold": 0.1,
    "IsPushToTalk": false
  }
}
```

### Noisy Environment (Push-to-Talk Recommended)

```json
{
  "AudioSettings": {
    "VolumeScale": 0.8,
    "InputVolumeScale": 1.5,
    "MinBroadcastThreshold": 0.0,
    "IsPushToTalk": true,
    "PushToTalkKey": "Space"
  }
}
```

## Troubleshooting

### Can't Connect to Server

- Check `Host` and `Port` values
- Ensure server is running and accessible
- Check firewall settings

### No Audio Input/Output

- Try setting `SelectedInputDevice` to `null`
- Check Windows audio device settings
- Restart the application

### Voice Always Transmitting

- Increase `MinBroadcastThreshold` (try 0.1 or 0.2)
- Or enable push-to-talk mode

### Can't Hear Other Players

- Check `VolumeScale` (should be 0.3-1.0)
- Verify you're on the same channel
- Check individual player volume in `PeerSettings`

### Wrong Microphone Being Used

- Set `SelectedInputDevice` to your specific microphone name
- Check Windows default recording device

## Notes

- Configuration changes require restarting the client
- `PeerSettings` are automatically managed by the client
- Invalid JSON will prevent the client from starting
- Use [jsonlint.com](https://jsonlint.com) to validate your JSON syntax
