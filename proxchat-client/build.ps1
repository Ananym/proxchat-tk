# Build script for ProxChat Client - Creates deployment-ready artifacts

param(
    [switch]$Clean,
    [switch]$NoTrim,
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"
$Output = ".\dist"

function Show-Help {
    Write-Host @"
ProxChat Client Deployment Build Script

Creates deployment-ready artifacts in a single versioned directory:

Usage:
  .\build.ps1 [options]

Options:
  -Clean           Clean build artifacts before building
  -NoTrim          Disable trimming (recommended, safer but larger file)
  -Verbose         Show detailed build output

Examples:
  .\build.ps1                    # Standard deployment build
  .\build.ps1 -Clean -NoTrim     # Clean build without trimming

Output Structure:
  deployment/
    ├── proxchattk v0.1.0/       # Portable app folder
    ├── proxchattk v0.1.0.zip    # Distribution zip
    ├── proxchattk v0.1.0.nupkg  # Update package
    └── RELEASES                 # Update index file

Deployment:
  • Upload RELEASES + .nupkg to your update server
  • Distribute .zip or app folder to users
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

function Build-For-Release {
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
            Write-Host "💡 Try building without trimming: .\build.ps1 -NoTrim" -ForegroundColor Yellow
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
        
        # Note: Config files are NOT copied to output directory
        # They will be added to distribution packages only (not update packages)
        
        return $exePath
    } else {
        throw "Build succeeded but executable not found at expected location: $exePath"
    }
}

function Get-ProjectVersion {
    # get version from project file
    $csprojContent = Get-Content "ProxChatClient.csproj" -Raw
    if ($csprojContent -match '<Version>([^<]+)</Version>') {
        return $matches[1]
    } else {
        Write-Host "Warning: Could not determine version from project, using default: 1.0.0" -ForegroundColor Yellow
        return "1.0.0"
    }
}

function Build-VelopackPackage {
    param($PublishPath, $Version)
    
    Write-Host "Creating Velopack update package..." -ForegroundColor Green
    
    # ensure we have vpk tool
    $vpkPath = Get-Command "vpk" -ErrorAction SilentlyContinue
    if (-not $vpkPath) {
        Write-Host "Installing Velopack tools..." -ForegroundColor Yellow
        & dotnet tool install -g vpk
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to install Velopack tools"
        }
    }
    
    # create temp releases directory for vpk
    $tempReleasesDir = ".\temp_releases"
    if (Test-Path $tempReleasesDir) {
        Remove-Item $tempReleasesDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $tempReleasesDir -Force | Out-Null
    
    # build vpk command
    $vpkArgs = @(
        "pack",
        "--packId", "ProxChatTK",
        "--packDir", $PublishPath,
        "--outputDir", $tempReleasesDir
    )
    
    Write-Host "Running: vpk $($vpkArgs -join ' ')" -ForegroundColor Cyan
    & vpk $vpkArgs
    
    if ($LASTEXITCODE -ne 0) {
        throw "Velopack packaging failed"
    }
    
    return $tempReleasesDir
}

function Organize-Artifacts {
    param($PublishPath, $TempReleasesPath, $Version)
    
    Write-Host "Organizing deployment artifacts..." -ForegroundColor Green
    
    # create main deployment directory
    $deploymentDir = ".\deployment"
    if (Test-Path $deploymentDir) {
        Remove-Item $deploymentDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $deploymentDir -Force | Out-Null
    
    # find the velopack-generated portable package
    $portableZip = Get-ChildItem -Path $TempReleasesPath -Filter "*-Portable.zip" | Select-Object -First 1
    if (-not $portableZip) {
        throw "Velopack portable package not found in $TempReleasesPath"
    }
    
    # extract the portable package as our base (contains Update.exe stub launcher)
    $versionedAppDir = Join-Path $deploymentDir "proxchattk v$Version"
    Write-Host "Extracting Velopack portable package..." -ForegroundColor Green
    Expand-Archive -Path $portableZip.FullName -DestinationPath $versionedAppDir -Force
    Write-Host "  ✓ Extracted with Update.exe stub launcher" -ForegroundColor Cyan
    
    # add distribution-only files that shouldn't be in update packages
    Write-Host "Adding distribution-only files..." -ForegroundColor Green
    
    # add config.json from config.json.default (for app root, persistent across updates)
    if (Test-Path "config.json.default") {
        $targetConfig = Join-Path $versionedAppDir "config.json"
        Copy-Item "config.json.default" $targetConfig -Force
        Write-Host "  ✓ config.json (from config.json.default) - persistent across updates" -ForegroundColor Cyan
    } else {
        Write-Host "  ⚠️  config.json.default not found" -ForegroundColor Yellow
    }
    
    # add VERSION.dll from memoryreadingdll build (app root level)
    $versionDllPath = "..\memoryreadingdll\build\Release\VERSION.dll"
    if (Test-Path $versionDllPath) {
        $targetVersionDll = Join-Path $versionedAppDir "VERSION.dll"
        Copy-Item $versionDllPath $targetVersionDll -Force
        $dllSize = [math]::Round((Get-Item $targetVersionDll).Length / 1KB, 2)
        Write-Host "  ✓ VERSION.dll ($dllSize KB) - for game directory" -ForegroundColor Cyan
    } else {
        Write-Host "  ⚠️  VERSION.dll not found at: $versionDllPath" -ForegroundColor Yellow
        Write-Host "     Run the main build_package.ps1 script first to build the DLL" -ForegroundColor Yellow
    }
    
    # add user guide (app root level)
    if (Test-Path "user_guide.txt") {
        $targetUserGuide = Join-Path $versionedAppDir "user_guide.txt"
        Copy-Item "user_guide.txt" $targetUserGuide -Force
        Write-Host "  ✓ user_guide.txt - usage instructions" -ForegroundColor Cyan
    } else {
        Write-Host "  ⚠️  user_guide.txt not found" -ForegroundColor Yellow
    }
    
    # add readme files if they exist (app root level)
    $readmeFiles = @("DISTRIBUTION-README.md", "CONFIG-README.md")
    foreach ($readme in $readmeFiles) {
        if (Test-Path $readme) {
            $targetReadme = Join-Path $versionedAppDir $readme
            Copy-Item $readme $targetReadme -Force
            Write-Host "  ✓ $readme" -ForegroundColor Cyan
        }
    }
    
    # create versioned zip (now includes all distribution files + stub launcher)
    $zipPath = Join-Path $deploymentDir "proxchattk v$Version.zip"
    try {
        Compress-Archive -Path "$versionedAppDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
        Write-Host "  ✓ Created distribution zip with stub launcher and all files" -ForegroundColor Cyan
    }
    catch {
        Write-Host "⚠️  Failed to create zip: $_" -ForegroundColor Yellow
    }
    
    # move and rename nupkg file (contains only core app files)
    $sourceNupkg = Get-ChildItem -Path $TempReleasesPath -Filter "*.nupkg" | Select-Object -First 1
    if ($sourceNupkg) {
        $targetNupkg = Join-Path $deploymentDir "proxchattk v$Version.nupkg"
        Copy-Item $sourceNupkg.FullName $targetNupkg -Force
        Write-Host "  ✓ Update package (.nupkg) - core app files only" -ForegroundColor Cyan
    }
    
    # move modern releases file (releases.win.json)
    $sourceReleases = Join-Path $TempReleasesPath "releases.win.json"
    if (Test-Path $sourceReleases) {
        $targetReleases = Join-Path $deploymentDir "releases.win.json"
        Copy-Item $sourceReleases $targetReleases -Force
        Write-Host "  ✓ Update index (releases.win.json) - modern format" -ForegroundColor Cyan
    } else {
        Write-Host "  ⚠️  releases.win.json not found" -ForegroundColor Yellow
    }
    
    # cleanup temp directory
    if (Test-Path $TempReleasesPath) {
        Remove-Item $TempReleasesPath -Recurse -Force
    }
    
    return $deploymentDir
}

function Show-Results {
    param($DeploymentPath, $Version)
    
    Write-Host ""
    Write-Host "✅ Deployment artifacts created successfully!" -ForegroundColor Green
    Write-Host ""
    
    $versionedAppDir = Join-Path $DeploymentPath "proxchattk v$Version"
    $zipPath = Join-Path $DeploymentPath "proxchattk v$Version.zip"
    $nupkgPath = Join-Path $DeploymentPath "proxchattk v$Version.nupkg"
    $releasesPath = Join-Path $DeploymentPath "releases.win.json"
    
    # show portable app folder
    if (Test-Path $versionedAppDir) {
        # check for stub launcher
        $stubPath = Join-Path $versionedAppDir "Update.exe"
        $currentDir = Join-Path $versionedAppDir "current"
        $exePath = Join-Path $currentDir "ProxChatClient.exe"
        
        if (Test-Path $stubPath) {
            Write-Host "📁 Portable app: proxchattk v$Version/" -ForegroundColor Cyan
            Write-Host "🚀 Update.exe (stub launcher) - users run this" -ForegroundColor Green
            
            if (Test-Path $exePath) {
                $exeInfo = Get-Item $exePath
                Write-Host "📊 Main app size: $([math]::Round($exeInfo.Length / 1MB, 2)) MB (in current/ folder)" -ForegroundColor Cyan
            }
        } else {
            # fallback for non-velopack structure
            $directExePath = Join-Path $versionedAppDir "ProxChatClient.exe"
            if (Test-Path $directExePath) {
                $exeInfo = Get-Item $directExePath
                Write-Host "📁 Portable app: proxchattk v$Version/" -ForegroundColor Cyan
                Write-Host "📊 Main exe size: $([math]::Round($exeInfo.Length / 1MB, 2)) MB" -ForegroundColor Cyan
            }
        }
        
        # show key distribution files at app root
        $versionDll = Join-Path $versionedAppDir "VERSION.dll"
        $userGuide = Join-Path $versionedAppDir "user_guide.txt"
        $configFile = Join-Path $versionedAppDir "config.json"
        
        if (Test-Path $versionDll) {
            $dllSize = [math]::Round((Get-Item $versionDll).Length / 1KB, 2)
            Write-Host "📄 Includes VERSION.dll ($dllSize KB) - copy to game directory" -ForegroundColor Cyan
        }
        
        if (Test-Path $userGuide) {
            Write-Host "📄 Includes user_guide.txt - setup instructions" -ForegroundColor Cyan
        }
        
        if (Test-Path $configFile) {
            Write-Host "📄 Includes config.json - persistent across updates" -ForegroundColor Cyan
        }
    }
    
    # show zip
    if (Test-Path $zipPath) {
        $zipInfo = Get-Item $zipPath
        Write-Host "📦 Distribution zip: proxchattk v$Version.zip" -ForegroundColor Cyan
        Write-Host "📊 Zip size: $([math]::Round($zipInfo.Length / 1MB, 2)) MB" -ForegroundColor Cyan
        Write-Host "   → Contains: Update.exe (stub) + app + VERSION.dll + user guide + config.json" -ForegroundColor Gray
    }
    
    # show update package
    if (Test-Path $nupkgPath) {
        $nupkgInfo = Get-Item $nupkgPath
        Write-Host "📦 Update package: proxchattk v$Version.nupkg" -ForegroundColor Cyan
        Write-Host "📊 Package size: $([math]::Round($nupkgInfo.Length / 1MB, 2)) MB" -ForegroundColor Cyan
        Write-Host "   → Contains: Core app files only (no VERSION.dll)" -ForegroundColor Gray
    }
    
    # show RELEASES file
    if (Test-Path $releasesPath) {
        Write-Host "📄 Update index: releases.win.json" -ForegroundColor Cyan
        Write-Host "   → Required for auto-updates" -ForegroundColor Gray
    }
    
    Write-Host ""
    Write-Host "📁 All artifacts in: $DeploymentPath" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "🚀 Ready to deploy!" -ForegroundColor Yellow
    Write-Host "• Upload RELEASES + .nupkg to your update server" -ForegroundColor White
    Write-Host "• Distribute .zip or app folder to users" -ForegroundColor White
    Write-Host "• Users copy VERSION.dll to their game directory" -ForegroundColor White
}

try {
    # Show help if no parameters
    if (-not ($Clean -or $NoTrim -or $Verbose -or $PSBoundParameters.Count -gt 0)) {
        Show-Help
        return
    }
    
    # Clean if requested
    if ($Clean) {
        Clean-BuildArtifacts
    }
    
    # Build release and create deployment artifacts
    Build-For-Release
    $version = Get-ProjectVersion
    $releasesDir = Build-VelopackPackage -PublishPath $Output -Version $version
    $deploymentDir = Organize-Artifacts -PublishPath $Output -TempReleasesPath $releasesDir -Version $version
    Show-Results -DeploymentPath $deploymentDir -Version $version
    
} catch {
    Write-Host "Build failed: $_" -ForegroundColor Red
    exit 1
} 