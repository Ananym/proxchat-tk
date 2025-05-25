# ProxChat Voice Chat System

A proximity-based voice chat system with WebSocket signaling server and Windows client.

## System Overview

- **Server**: Rust WebSocket signaling server with channel-based proximity matching
- **Client**: .NET 8 WPF Windows application with memory reading and voice chat
- **Memory Reading**: C++ DLL for reading player coordinates from memory

## Quick Start

### Complete Package Build

```powershell
# Build everything and create distribution package
.\build_package.ps1 -Version "v1.0"
```

### Server Deployment Options

#### Option 1: VPS Deployment (Recommended - Cost Effective)

```powershell
# Build Linux binary for VPS deployment
cd server
.\build.ps1 -DockerExtract

# Upload dist/prox-chat-server-linux to your VPS
# See server/VPS-DEPLOYMENT-GUIDE.md for complete instructions
```

**VPS Benefits:**

- 60-80% less expensive than containers
- $3-6/month vs $15-30/month for AWS Fargate
- Simple binary deployment
- Perfect for small-scale usage

#### Option 2: Container Deployment (AWS ECS/Fargate)

```powershell
# Multi-architecture container build
cd server
.\build.ps1 -Docker -MultiArch -Push -Registry "your-registry" -Tag "v1.0"

# See server/AWS-deployment-guide.md for complete instructions
```

### Client Setup

```powershell
# Build optimized single-file executable
cd proxchat-client
.\build.ps1 -Release

# Edit config.json to point to your server
# Requires .NET 8 Desktop Runtime on target machines
```

## Repository Structure

```
prox-chat-tk/
├── server/                     # Rust signaling server
│   ├── build.ps1              # Build script with VPS + container support
│   ├── extract-binary.ps1     # Extract Linux binary from Docker image
│   ├── VPS-DEPLOYMENT-GUIDE.md # Complete VPS deployment guide
│   └── AWS-deployment-guide.md # Container deployment guide
├── proxchat-client/           # .NET 8 WPF client
│   ├── build.ps1             # Client build script
│   ├── config.json.default   # Default configuration
│   └── CONFIG-README.md      # Configuration guide
├── memoryreadingdll/          # C++ memory reading component
│   └── build.ps1             # DLL build script
├── build_package.ps1          # Complete package build script
└── PACKAGE-BUILD-README.md    # Package build documentation
```

## Configuration

### Server Port Configuration

- Default port: **8080**
- Firewall configuration varies by VPS provider
- See `server/VPS-DEPLOYMENT-GUIDE.md` for port setup instructions

### Client Configuration

Edit `config.json`:

```json
{
  "WebSocketServer": {
    "Host": "your-server-ip-or-domain",
    "Port": 8080
  },
  "Channel": 0,
  "AudioSettings": {
    "VolumeScale": 0.5,
    "InputVolumeScale": 1.0,
    "IsPushToTalk": false,
    "PushToTalkKey": "F12"
  }
}
```

## Channel System

Players are separated into different voice chat channels:

- **Channel 0**: Default channel for all new connections
- **Channel 1, 2, 3...**: Separate voice chat groups
- Players only hear others in the same channel
- Configurable per client in `config.json`

## Dependencies

### Server Dependencies

- **Standalone binary** - only requires basic system libraries
- Linux: `glibc`, `libssl`, `libcrypto` (usually pre-installed)
- **No special runtime** required

### Client Dependencies

- **Windows 10 1809+** (x64 only)
- **.NET 8.0 Desktop Runtime** (framework-dependent deployment)
- **Microphone and speakers**

## Build Requirements

### For Server:

- **Rust** (for local builds)
- **Docker** (for VPS binary extraction - recommended)

### For Client:

- **.NET 8 SDK**
- **Visual Studio Build Tools** (for memory reading DLL)
- **CMake**

## Cost Comparison

| Deployment Option | Monthly Cost | Setup Complexity | Scalability |
| ----------------- | ------------ | ---------------- | ----------- |
| **VPS Binary**    | $3-6         | Low              | Manual      |
| **AWS Fargate**   | $15-30       | Medium           | Automatic   |
| **Local Hosting** | $0           | Low              | None        |

## Documentation

- 📦 [Package Build Guide](PACKAGE-BUILD-README.md)
- 🖥️ [VPS Deployment Guide](server/VPS-DEPLOYMENT-GUIDE.md)
- ☁️ [AWS Container Deployment](server/AWS-deployment-guide.md)
- ⚙️ [Client Configuration](proxchat-client/CONFIG-README.md)
- 🚀 [Distribution Guide](proxchat-client/DISTRIBUTION-README.md)

## Troubleshooting

### Server Issues

- **Connection refused**: Check firewall rules and VPS provider settings
- **Port conflicts**: Verify port 8080 is available
- **Missing dependencies**: Install `openssl` and `libssl3`

### Client Issues

- **Audio problems**: Check microphone permissions and audio device selection
- **Connection timeouts**: Verify server address and network connectivity
- **Missing .NET runtime**: Install .NET 8.0 Desktop Runtime

## Development Workflow

1. **Make changes** to server or client code
2. **Test locally** with `.\build.ps1 -Docker -Run` (server) and `.\build.ps1 -Debug` (client)
3. **Build for deployment**:
   - VPS: `.\build.ps1 -DockerExtract`
   - Container: `.\build.ps1 -Docker -MultiArch`
4. **Create distribution package** with `.\build_package.ps1 -Version "vX.X"`
5. **Deploy and test** on target environment

## License

[Add your license information here]
