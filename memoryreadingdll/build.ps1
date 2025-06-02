# PowerShell build script for memoryreadingdll (Release Mode)

# Get the directory where the script is located
$ScriptDir = Split-Path -Path $MyInvocation.MyCommand.Definition -Parent

# Define the build directory name
$BuildDirName = "build"
$BuildDir = Join-Path -Path $ScriptDir -ChildPath $BuildDirName

# Define the build configuration
$BuildConfig = "Release"

# Define the target directory for the final DLL
$TargetGameDir = "C:\Program Files (x86)\KRU\NexusTK"
$OutputDLLName = "VERSION.dll"
$SourceDLLPath = Join-Path -Path $BuildDir -ChildPath "$BuildConfig\$OutputDLLName"
$DestinationDLLPath = Join-Path -Path $TargetGameDir -ChildPath $OutputDLLName

# --- Clean previous build (optional but recommended) ---
if (Test-Path -Path $BuildDir) {
    Write-Host "Removing previous build directory: $BuildDir"
    Remove-Item -Path $BuildDir -Recurse -Force
}

# --- Create build directory ---
Write-Host "Creating build directory: $BuildDir"
New-Item -Path $BuildDir -ItemType Directory -Force | Out-Null

# --- Run CMake configuration --- 
Write-Host "Running CMake configuration (Generator: VS 17 2022, Platform: Win32, with vcpkg)..."
Push-Location -Path $BuildDir

# check if vcpkg toolchain exists
$VcpkgToolchain = Join-Path -Path $ScriptDir -ChildPath "..\vcpkg\scripts\buildsystems\vcpkg.cmake"
if (Test-Path -Path $VcpkgToolchain) {
    Write-Host "Using vcpkg toolchain: $VcpkgToolchain"
    cmake .. -G "Visual Studio 17 2022" -A Win32 -DCMAKE_TOOLCHAIN_FILE="$VcpkgToolchain"
} else {
    Write-Warning "vcpkg toolchain not found at $VcpkgToolchain - building without vcpkg"
    cmake .. -G "Visual Studio 17 2022" -A Win32 
}

$CMakeConfigResult = $?
Pop-Location

if (-not $CMakeConfigResult) {
    Write-Error "CMake configuration failed."
    exit 1
}

Write-Host "CMake configuration completed successfully."

# --- Run CMake build --- 
# Specify the configuration to build using --config
Write-Host "Running CMake build (Configuration: $BuildConfig)..."
cmake --build $BuildDir --config $BuildConfig
$CMakeBuildResult = $?

if (-not $CMakeBuildResult) {
    Write-Error "CMake build failed."
    exit 1
}

Write-Host "CMake build completed successfully."
Write-Host "Output DLL located at: $SourceDLLPath"

# --- Copy DLL to Target Directory ---
Write-Host "Attempting to copy $OutputDLLName to $TargetGameDir..."
if (-not (Test-Path -Path $SourceDLLPath)) {
    Write-Error "Build succeeded but output DLL not found at $SourceDLLPath. Cannot copy."
    exit 1
}

if (-not (Test-Path -Path $TargetGameDir)) {
    Write-Warning "Target directory $TargetGameDir does not exist. Skipping copy."
    # Or should we attempt to create it? For now, skip.
    # New-Item -Path $TargetGameDir -ItemType Directory -Force
} else {
    try {
        Copy-Item -Path $SourceDLLPath -Destination $DestinationDLLPath -Force -ErrorAction Stop
        Write-Host "Successfully copied $OutputDLLName to $TargetGameDir."
    } catch {
        Write-Error "Failed to copy $OutputDLLName to $TargetGameDir. Error: $_"
        Write-Warning "This often requires running the script with Administrator privileges."
        exit 1
    }
}

Write-Host "Build and copy process finished."
exit 0 