# ProxChat Client - Distribution Package

This package contains the ProxChat proximity voice chat client for Windows.

## Files Included

- `ProxChatClient.exe` - Main application (self-contained)
- `config.json.default` - Configuration template
- `config.json` - Pre-configured settings (if provided)

## Quick Setup

### First Time Setup

1. **Copy the configuration template:**

   ```
   copy config.json.default config.json
   ```

2. **Edit `config.json`** to match your server:

   ```json
   {
     "WebSocketServer": {
       "Host": "your-server-address.com",
       "Port": 8080
     },
     "Channel": 0
   }
   ```

3. **Run the application:**
   ```
   ProxChatClient.exe
   ```

### If config.json Already Exists

Just run `ProxChatClient.exe` - your settings are ready to use!

## System Requirements

- **OS:** Windows 10 version 1809 or later
- **Architecture:** x64 (64-bit)
- **Runtime:** .NET 8.0 Desktop Runtime (download from Microsoft)
- **Network:** Internet connection to reach voice chat server
- **Audio:** Microphone and speakers/headphones

### Installing .NET Runtime

If the application won't start, you need to install .NET 8.0:

1. Visit: https://dotnet.microsoft.com/download/dotnet/8.0
2. Download: ".NET Desktop Runtime 8.0.x" for Windows x64
3. Run the installer
4. Restart the ProxChat application

## Configuration

For detailed configuration options, see the included `CONFIG-README.md` or visit:
[Configuration Guide](CONFIG-README.md)

### Quick Settings

**Server Connection:**

- Change `Host` to your game server's IP/hostname
- Default port is usually `8080`

**Voice Chat Channel:**

- `"Channel": 0` = Default channel
- `"Channel": 1` = Private group
- Only players on the same channel can hear each other

**Audio Controls:**

- `"VolumeScale": 0.5` = Master volume (0.0 to 1.0)
- `"IsPushToTalk": false` = Voice activation
- `"IsPushToTalk": true` = Push button to talk

## Troubleshooting

### Common Issues

**"Application won't start" or "Missing dependencies"**

- Install .NET 8.0 Desktop Runtime (see above)
- Restart your computer after installation

**"Connection failed"**

- Check if the server address and port are correct
- Ensure the voice chat server is running
- Check firewall/antivirus settings

**"No microphone detected"**

- Check Windows sound settings
- Set `"SelectedInputDevice": null` in config
- Restart the application

**"Can't hear other players"**

- Verify you're on the same channel as other players
- Check `"VolumeScale"` setting (try 0.7)
- Ensure other players are nearby in the game

**"Voice always transmitting"**

- Increase `"MinBroadcastThreshold"` to 0.1 or 0.2
- Or enable push-to-talk mode

### Getting Help

1. Check the configuration guide: `CONFIG-README.md`
2. Verify JSON syntax at: https://jsonlint.com
3. Contact your server administrator for connection details

## Security & Privacy

- The application only connects to the specified voice chat server
- No personal data is collected or transmitted
- Voice data is sent only to nearby players in your proximity
- All communication is encrypted in transit

## Uninstall

To remove the application:

1. Delete the `ProxChatClient.exe` file
2. Delete any configuration files if desired
3. No registry cleanup needed - it's a portable application

---

**Version:** Built with .NET 8.0 for Windows x64  
**Type:** Framework-dependent single-file application  
**Dependencies:** .NET 8.0 Desktop Runtime required
