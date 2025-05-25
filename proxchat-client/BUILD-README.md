# ProxChat Client Build Guide (Windows)

This document explains how to build the ProxChat client for Windows using the included build script.

## Quick Start

```powershell
# Optimized single-file executable (recommended for distribution)
.\build.ps1 -Release -NoTrim

# Development build and run
.\build.ps1 -Debug -Run
```

## Build Script Options

### Basic Commands

- `.\build.ps1` - Show help and available options
- `.\build.ps1 -Release` - Create optimized release build
- `.\build.ps1 -Debug` - Create debug build for development
- `.\build.ps1 -Clean` - Clean all build artifacts

### Advanced Options

- `-NoTrim` - Disable trimming (recommended for WPF, safer but larger file)
- `-Output <path>` - Custom output directory (default: .\dist)
- `-Run` - Run the application after building
- `-Verbose` - Show detailed build output

## Build Types

### Release Build (Recommended)

```powershell
.\build.ps1 -Release -NoTrim
```

**Features:**

- Single executable file (~44MB)
- Optimized for performance
- Ready for distribution
- Requires .NET 8 runtime on target machines

**Output:** `.\dist\ProxChatClient.exe`

### Debug Build

```powershell
.\build.ps1 -Debug
```

**Features:**

- Faster build time
- Better debugging support
- Standard .NET output structure
- Requires .NET runtime on target machine

**Output:** `.\bin\Debug\net8.0-windows10.0.17763.0\win-x64\`

## Trimming Information

### Why -NoTrim is Recommended

WPF applications have limited support for .NET trimming due to:

- Dynamic type loading in WPF
- Reflection usage in UI frameworks
- Third-party libraries that aren't trim-compatible

### If You Want to Try Trimming

```powershell
# Enable trimming (experimental, may not work)
.\build.ps1 -Release
```

⚠️ **Warning:** If the build fails with trimming enabled, use `-NoTrim`:

```powershell
.\build.ps1 -Release -NoTrim
```

## File Sizes

| Build Type        | Typical Size | Notes                                        |
| ----------------- | ------------ | -------------------------------------------- |
| Release (No Trim) | ~44MB        | Framework-dependent, requires .NET 8 runtime |
| Release (Trimmed) | ~30-40MB     | Experimental, may have runtime issues        |
| Debug             | ~100MB+      | Separate files, not optimized                |

## Examples

### Distribution Build

```powershell
# Clean and create distribution-ready executable
.\build.ps1 -Clean -Release -NoTrim
```

### Development Workflow

```powershell
# Quick test build
.\build.ps1 -Debug -Run
```

## Troubleshooting

### Build Fails with Trimming

```
Error: WPF is not supported with trimming enabled
```

**Solution:** Use `-NoTrim` flag

### Missing Dependencies

```
Error: Could not find a part of the path...
```

**Solution:** Run `dotnet restore` first, or use `-Clean` flag

### Large File Size

The single-file executable includes the entire .NET runtime and all dependencies. This is normal for self-contained applications and allows the app to run on machines without .NET installed.

## Distribution

After building with `-Release -NoTrim`, you can distribute:

- `.\dist\ProxChatClient.exe` - Main executable
- `.\dist\config.json.default` - Configuration template
- `.\dist\config.json` - Your configuration (if present)

### For End Users

Users should:

1. Copy `config.json.default` to `config.json`
2. Edit `config.json` with their server settings
3. Run `ProxChatClient.exe`

See `CONFIG-README.md` for detailed configuration instructions.

The executable is completely self-contained and requires no additional installation on target Windows machines.

## System Requirements

- **Target OS:** Windows 10 version 1809 (build 17763) or later
- **Architecture:** x64 only
- **Runtime:** .NET 8.0 Desktop Runtime (required)
- **Dependencies:** Download from https://dotnet.microsoft.com/download/dotnet/8.0

## Performance Notes

- **Release builds** are significantly faster than debug builds
- **ReadyToRun compilation** improves startup time
- **Single-file compression** reduces disk usage
- **No trimming** ensures maximum compatibility
