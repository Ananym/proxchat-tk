# ProxChat Complete Package Build Script
# Builds memory reading DLL, client, and creates distribution package

param(
    [switch]$Clean,
    [switch]$NoZip,
    [string]$Version = "latest",
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"

# Paths
$RootDir = Get-Location
$DistDir = Join-Path $RootDir "dist"
$ReleaseDir = Join-Path $DistDir "release"
$MemoryDllDir = Join-Path $RootDir "memoryreadingdll"
$ClientDir = Join-Path $RootDir "proxchat-client"

function Write-Header {
    param($Message)
    Write-Host ""
    Write-Host "=" * 60 -ForegroundColor Cyan
    Write-Host $Message -ForegroundColor Green
    Write-Host "=" * 60 -ForegroundColor Cyan
}

function Write-Step {
    param($Message)
    Write-Host ""
    Write-Host ">>> $Message" -ForegroundColor Yellow
}

function Show-Help {
    Write-Host @"
ProxChat Complete Package Build Script

Usage:
  .\build_package.ps1 [options]

Options:
  -Clean           Clean all build artifacts before building
  -NoZip           Skip creating the ZIP archive
  -Version <ver>   Version string for the package (default: latest)
  -Verbose         Show detailed build output

Examples:
  .\build_package.ps1                    # Build complete package
  .\build_package.ps1 -Clean             # Clean build everything
  .\build_package.ps1 -Version "v1.0"    # Build with version tag
  .\build_package.ps1 -NoZip             # Build without creating ZIP

Output:
  Creates: .\dist\release\* (distribution files)
  Creates: .\dist\ProxChat-$Version.zip (if -NoZip not specified)
"@
}

function Clean-All {
    Write-Step "Cleaning all build artifacts..."
    
    if (Test-Path $DistDir) {
        Remove-Item -Recurse -Force $DistDir
        Write-Host "Removed dist directory"
    }
    
    # Clean memory reading DLL
    Push-Location $MemoryDllDir
    try {
        if (Test-Path "build") {
            Remove-Item -Recurse -Force "build"
            Write-Host "Cleaned memory reading DLL build directory"
        }
    } finally {
        Pop-Location
    }
    
    # Clean client
    Push-Location $ClientDir
    try {
        & .\build.ps1 -Clean | Out-Null
        Write-Host "Cleaned client build artifacts"
    } finally {
        Pop-Location
    }
}

function Build-MemoryDll {
    Write-Step "Building memory reading DLL..."
    
    Push-Location $MemoryDllDir
    try {
        if ($Verbose) {
            & .\build.ps1
        } else {
            & .\build.ps1 | Out-Null
        }
        
        if ($LASTEXITCODE -ne 0) {
            throw "Memory reading DLL build failed"
        }
        
        $dllPath = "build\Release\VERSION.dll"
        if (-not (Test-Path $dllPath)) {
            throw "Memory reading DLL not found at expected location: $dllPath"
        }
        
        Write-Host "✅ Memory reading DLL built successfully" -ForegroundColor Green
        return (Resolve-Path $dllPath).Path
    } finally {
        Pop-Location
    }
}

function Build-Client {
    Write-Step "Building ProxChat client..."
    
    Push-Location $ClientDir
    try {
        if ($Verbose) {
            & .\build.ps1 -Release -NoTrim
        } else {
            & .\build.ps1 -Release -NoTrim | Out-Null
        }
        
        if ($LASTEXITCODE -ne 0) {
            throw "Client build failed"
        }
        
        $exePath = "dist\ProxChatClient.exe"
        $configPath = "config.json.default"
        
        if (-not (Test-Path $exePath)) {
            throw "Client executable not found at expected location: $exePath"
        }
        
        if (-not (Test-Path $configPath)) {
            throw "Default config not found at expected location: $configPath"
        }
        
        Write-Host "✅ ProxChat client built successfully" -ForegroundColor Green
        return @{
            Exe = (Resolve-Path $exePath).Path
            Config = (Resolve-Path $configPath).Path
        }
    } finally {
        Pop-Location
    }
}

function Create-DistributionPackage {
    param(
        $DllPath,
        $ClientPaths
    )
    
    Write-Step "Creating distribution package..."
    
    # Create distribution directories
    if (-not (Test-Path $DistDir)) {
        New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
    }
    
    if (Test-Path $ReleaseDir) {
        Remove-Item -Recurse -Force $ReleaseDir
    }
    New-Item -ItemType Directory -Path $ReleaseDir -Force | Out-Null
    
    # Copy files to distribution folder
    Write-Host "Copying files to distribution folder..."
    
    # Copy client executable
    $targetExe = Join-Path $ReleaseDir "ProxChatClient.exe"
    Copy-Item $ClientPaths.Exe $targetExe -Force
    $exeSize = [math]::Round((Get-Item $targetExe).Length / 1MB, 2)
    Write-Host "  ✓ ProxChatClient.exe ($exeSize MB)" -ForegroundColor Cyan
    
    # Copy memory reading DLL
    $targetDll = Join-Path $ReleaseDir "VERSION.dll"
    Copy-Item $DllPath $targetDll -Force
    $dllSize = [math]::Round((Get-Item $targetDll).Length / 1KB, 2)
    Write-Host "  ✓ VERSION.dll ($dllSize KB)" -ForegroundColor Cyan
    
    # Copy and rename config file (remove .default extension)
    $targetConfig = Join-Path $ReleaseDir "config.json"
    Copy-Item $ClientPaths.Config $targetConfig -Force
    Write-Host "  ✓ config.json (default configuration)" -ForegroundColor Cyan
    
    # Copy documentation from client directory
    $clientDocs = @(
        "CONFIG-README.md",
        "DISTRIBUTION-README.md"
    )
    
    foreach ($doc in $clientDocs) {
        $sourcePath = Join-Path $ClientDir $doc
        if (Test-Path $sourcePath) {
            $targetPath = Join-Path $ReleaseDir $doc
            Copy-Item $sourcePath $targetPath -Force
            Write-Host "  ✓ $doc" -ForegroundColor Cyan
        }
    }
    
    Write-Host "✅ Distribution package created in: $ReleaseDir" -ForegroundColor Green
}

function Create-Archive {
    param($Version)
    
    Write-Step "Creating ZIP archive..."
    
    $zipName = "ProxChat-$Version.zip"
    $zipPath = Join-Path $DistDir $zipName
    
    # Remove existing ZIP if it exists
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }
    
    # Create ZIP archive
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($ReleaseDir, $zipPath)
        
        $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
        Write-Host "✅ ZIP archive created: $zipName ($zipSize MB)" -ForegroundColor Green
        Write-Host "📦 Location: $zipPath" -ForegroundColor Cyan
        
        return $zipPath
    } catch {
        Write-Warning "Failed to create ZIP archive: $_"
        Write-Host "Distribution files are available in: $ReleaseDir" -ForegroundColor Yellow
    }
}

function Show-Summary {
    param($ZipPath)
    
    Write-Header "BUILD COMPLETE"
    
    Write-Host "📁 Distribution files:" -ForegroundColor Green
    Get-ChildItem $ReleaseDir | ForEach-Object {
        $size = if ($_.PSIsContainer) { "[DIR]" } else { 
            $sizeKB = [math]::Round($_.Length / 1KB, 1)
            "($sizeKB KB)"
        }
        Write-Host "  • $($_.Name) $size" -ForegroundColor Cyan
    }
    
    if ($ZipPath -and (Test-Path $ZipPath)) {
        Write-Host ""
        Write-Host "📦 Ready for distribution:" -ForegroundColor Green
        Write-Host "  $ZipPath" -ForegroundColor Cyan
    }
    
    Write-Host ""
    Write-Host "🚀 Package ready for distribution!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Yellow
    Write-Host "1. Test the package on a clean machine" -ForegroundColor Yellow
    Write-Host "2. Verify .NET 8 runtime installation works" -ForegroundColor Yellow
    Write-Host "3. Distribute the ZIP file or folder contents" -ForegroundColor Yellow
}

# Main execution
try {
    if ($PSBoundParameters.Count -eq 0 -and -not $Clean) {
        Show-Help
        return
    }
    
    Write-Header "PROXCHAT PACKAGE BUILD"
    
    # Clean if requested
    if ($Clean) {
        Clean-All
    }
    
    # Build components
    $dllPath = Build-MemoryDll
    $clientPaths = Build-Client
    
    # Create distribution package
    Create-DistributionPackage -DllPath $dllPath -ClientPaths $clientPaths
    
    # Create ZIP archive unless skipped
    $zipPath = $null
    if (-not $NoZip) {
        $zipPath = Create-Archive -Version $Version
    }
    
    # Show summary
    Show-Summary -ZipPath $zipPath
    
} catch {
    Write-Host ""
    Write-Host "❌ Build failed: $_" -ForegroundColor Red
    exit 1
} 