# Build script for ProxChat Client (Windows only)

param(
    [switch]$Release,
    [switch]$Debug,
    [switch]$Run,
    [switch]$Clean,
    [string]$Output = ".\dist",
    [switch]$NoTrim,
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"

function Show-Help {
    Write-Host @"
ProxChat Client Build Script (Windows x64)

Usage:
  .\build.ps1 [options]

Options:
  -Release         Build optimized release version (single-file)
  -Debug           Build debug version for development
  -Run             Run the application after building
  -Clean           Clean build artifacts before building
  -Output <path>   Output directory (default: .\dist)
  -NoTrim          Disable trimming (recommended, safer but larger file)
  -Verbose         Show detailed build output

Examples:
  .\build.ps1 -Release -NoTrim          # Single-file build (~44MB, requires .NET 8)
  .\build.ps1 -Debug -Run               # Debug build and run
  .\build.ps1 -Clean -Release -NoTrim   # Clean and release build

Output:
  Release builds create a single executable in the dist folder
  Debug builds use the standard bin/Debug folder
"@
}

function Clean-BuildArtifacts {
    Write-Host "Cleaning build artifacts..." -ForegroundColor Yellow
    
    if (Test-Path "bin") {
        Remove-Item -Recurse -Force "bin"
        Write-Host "Removed bin directory"
    }
    
    if (Test-Path "obj") {
        Remove-Item -Recurse -Force "obj"
        Write-Host "Removed obj directory"
    }
    
    if (Test-Path $Output) {
        Remove-Item -Recurse -Force $Output
        Write-Host "Removed output directory: $Output"
    }
}

function Build-Debug {
    Write-Host "Building debug version..." -ForegroundColor Green
    
    $buildArgs = @("build", "--configuration", "Debug")
    if ($Verbose) { $buildArgs += "--verbosity", "detailed" }
    
    & dotnet $buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Debug build failed"
    }
    
    Write-Host "Debug build completed successfully!" -ForegroundColor Green
    Write-Host "Executable location: .\bin\Debug\net8.0-windows10.0.17763.0\win-x64\ProxChatClient.exe"
}

function Build-Release {
    Write-Host "Building optimized release version..." -ForegroundColor Green
    Write-Host "This may take a few minutes due to optimization..." -ForegroundColor Yellow
    
    # Ensure output directory exists
    if (-not (Test-Path $Output)) {
        New-Item -ItemType Directory -Path $Output -Force | Out-Null
    }
    
    $publishArgs = @(
        "publish",
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "false",
        "--output", $Output
    )
    
    if (-not $NoTrim) {
        $publishArgs += "--property:EnableTrimming=true"
        Write-Host "⚠️  Trimming enabled - Note: WPF trimming is experimental and may cause issues" -ForegroundColor Yellow
        Write-Host "   If you encounter problems, use -NoTrim flag to disable trimming" -ForegroundColor Yellow
    } else {
        Write-Host "Trimming disabled - safer for WPF but larger file size" -ForegroundColor Cyan
    }
    
    if ($Verbose) { 
        $publishArgs += "--verbosity", "detailed" 
    }
    
    & dotnet $publishArgs
    if ($LASTEXITCODE -ne 0) {
        if (-not $NoTrim) {
            Write-Host ""
            Write-Host "❌ Build failed with trimming enabled." -ForegroundColor Red
            Write-Host "💡 Try building without trimming: .\build.ps1 -Release -NoTrim" -ForegroundColor Yellow
        }
        throw "Release build failed"
    }
    
    # Find the executable
    $exePath = Join-Path $Output "ProxChatClient.exe"
    
    if (Test-Path $exePath) {
        $fileInfo = Get-Item $exePath
        $fileSizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
        
        Write-Host ""
        Write-Host "✅ Release build completed successfully!" -ForegroundColor Green
        Write-Host "📁 Location: $exePath" -ForegroundColor Cyan
        Write-Host "📊 File size: $fileSizeMB MB" -ForegroundColor Cyan
        Write-Host "📦 Framework-dependent build - requires .NET 8 runtime" -ForegroundColor Yellow
        Write-Host "🚀 Single-file executable ready!" -ForegroundColor Green
        
        # Copy config files to output
        if (Test-Path "config.json") {
            Copy-Item "config.json" $Output -Force
            Write-Host "📋 Config file copied to output directory" -ForegroundColor Cyan
        }
        
        if (Test-Path "config.json.default") {
            Copy-Item "config.json.default" $Output -Force
            Write-Host "📋 Default config template copied to output directory" -ForegroundColor Cyan
        }
        
        # Provide user guidance about configuration
        if (-not (Test-Path (Join-Path $Output "config.json"))) {
            Write-Host ""
            Write-Host "💡 No config.json found. Users should:" -ForegroundColor Yellow
            Write-Host "   1. Copy config.json.default to config.json" -ForegroundColor Yellow
            Write-Host "   2. Edit config.json with their server settings" -ForegroundColor Yellow
        }
        
        return $exePath
    } else {
        throw "Build succeeded but executable not found at expected location: $exePath"
    }
}

function Run-Application {
    param($ExecutablePath)
    
    if ($Release) {
        if ($ExecutablePath -and (Test-Path $ExecutablePath)) {
            Write-Host "Running release build..." -ForegroundColor Green
            & $ExecutablePath
        } else {
            throw "Release executable not found. Build may have failed."
        }
    } else {
        Write-Host "Running debug build..." -ForegroundColor Green
        & dotnet run --configuration Debug
    }
}

try {
    # Show help if no parameters
    if (-not ($Release -or $Debug -or $Clean -or $PSBoundParameters.Count -gt 0)) {
        Show-Help
        return
    }
    
    # Clean if requested
    if ($Clean) {
        Clean-BuildArtifacts
    }
    
    $builtExecutable = $null
    
    # Build based on configuration
    if ($Release) {
        $builtExecutable = Build-Release
    } elseif ($Debug) {
        Build-Debug
    } else {
        # Default to release if neither specified but other options given
        Write-Host "No build type specified, defaulting to Release build..." -ForegroundColor Yellow
        $builtExecutable = Build-Release
    }
    
    # Run if requested
    if ($Run) {
        Run-Application -ExecutablePath $builtExecutable
    }
    
} catch {
    Write-Host "Build failed: $_" -ForegroundColor Red
    exit 1
} 