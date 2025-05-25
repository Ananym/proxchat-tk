# ProxChat Package Build System

This document explains how to build complete distribution packages for ProxChat.

## Quick Start

```powershell
# Build complete package with ZIP archive
.\build_package.ps1 -Version "v1.0"

# Clean build everything
.\build_package.ps1 -Clean -Version "v1.0"
```

## What It Does

The `build_package.ps1` script:

1. **Builds the memory reading DLL** (`memoryreadingdll/build.ps1`)
2. **Builds the ProxChat client** (`proxchat-client/build.ps1`)
3. **Creates distribution folder** (`dist/release/`)
4. **Copies all required files:**
   - `ProxChatClient.exe` (~44MB)
   - `VERSION.dll` (~51KB)
   - `config.json` (renamed from config.json.default)
   - `CONFIG-README.md`
   - `DISTRIBUTION-README.md`
5. **Creates ZIP archive** (`dist/ProxChat-{version}.zip`)

## Usage

### Basic Commands

```powershell
# Show help
.\build_package.ps1

# Build package
.\build_package.ps1 -Version "v1.0"

# Clean build
.\build_package.ps1 -Clean -Version "v1.0"

# Build without ZIP (folder only)
.\build_package.ps1 -NoZip

# Verbose output
.\build_package.ps1 -Verbose -Version "v1.0"
```

### Options

- **`-Clean`** - Remove all build artifacts before building
- **`-NoZip`** - Skip creating the ZIP archive
- **`-Version <string>`** - Version tag for the package (default: "latest")
- **`-Verbose`** - Show detailed build output

## Output Structure

### Distribution Folder: `dist/release/`

```
dist/release/
├── ProxChatClient.exe          # Main application (~44MB)
├── VERSION.dll                 # Memory reading DLL (~51KB)
├── config.json                 # Default configuration
├── CONFIG-README.md            # Configuration guide
└── DISTRIBUTION-README.md      # End-user instructions
```

### ZIP Archive: `dist/ProxChat-{version}.zip`

- Contains all files from the release folder
- Compressed to ~17MB
- Ready for distribution

## Prerequisites

Before running the package build:

### For Memory Reading DLL:

- **Visual Studio Build Tools** or Visual Studio
- **CMake** (for building the DLL)
- **Windows SDK**

### For ProxChat Client:

- **.NET 8 SDK** (for building)
- **PowerShell** (for build scripts)

### Verification:

```powershell
# Check if CMake is available
cmake --version

# Check if .NET 8 SDK is available
dotnet --version

# Check if MSBuild is available
where msbuild
```

## Build Process Details

### 1. Memory Reading DLL Build

- Uses CMake to generate Visual Studio project
- Builds with Visual Studio compiler in Release mode
- Outputs `VERSION.dll` to `memoryreadingdll/build/Release/`

### 2. ProxChat Client Build

- Uses .NET publish with framework-dependent deployment
- Creates single-file executable (~44MB)
- Requires .NET 8 runtime on target machines
- Outputs to `proxchat-client/dist/`

### 3. Package Assembly

- Creates clean distribution folder
- Copies all required files
- Renames `config.json.default` to `config.json`
- Includes documentation files

### 4. Archive Creation

- Uses .NET's built-in ZIP compression
- Results in ~17MB archive from ~45MB of files
- Suitable for download/distribution

## Troubleshooting

### "Memory reading DLL build failed"

- Ensure Visual Studio Build Tools are installed
- Check CMake is in PATH
- Verify Windows SDK is available

### "Client build failed"

- Ensure .NET 8 SDK is installed
- Check PowerShell execution policy
- Try building client manually first

### "Build succeeded but executable not found"

- Check antivirus didn't quarantine files
- Verify disk space is available
- Try running with `-Verbose` flag

### ZIP Creation Failed

- Verify no files are locked/in use
- Check disk space for ZIP creation
- Files will still be available in `dist/release/`

## Distribution

After successful build:

### For Internal Testing:

Use the `dist/release/` folder directly

### For Public Distribution:

Use the ZIP file: `dist/ProxChat-{version}.zip`

### End User Requirements:

- Windows 10 1809+ (x64)
- .NET 8.0 Desktop Runtime
- Microphone and speakers

## Version Management

Use semantic versioning for releases:

```powershell
.\build_package.ps1 -Version "v1.0.0"      # Major release
.\build_package.ps1 -Version "v1.1.0"      # Minor update
.\build_package.ps1 -Version "v1.1.1"      # Patch/hotfix
.\build_package.ps1 -Version "v2.0.0-beta" # Pre-release
```

## Automation

For CI/CD pipelines:

```powershell
# Clean build with error handling
try {
    .\build_package.ps1 -Clean -Version $env:BUILD_VERSION
    Write-Host "Build successful"
} catch {
    Write-Error "Build failed: $_"
    exit 1
}
```
