# Build script for ProxChat Client - Creates deployment-ready artifacts

param(
    [switch]$Clean,
    [switch]$Verbose,
    [switch]$Help
)

$ErrorActionPreference = "Stop"
$TempOutput = ".\deployment\temp\publish"  # temporary dotnet publish output

function Show-Help {
    Write-Host @"
ProxChat Client Deployment Build Script

Creates deployment-ready artifacts in a single versioned directory:

Usage:
  .\build.ps1 [options]

Options:
  -Clean           Clean build artifacts before building
  -Verbose         Show detailed build output
  -Help            Show this help message

Examples:
  .\build-deployment.ps1                    # Standard deployment build
  .\build-deployment.ps1 -Clean             # Clean build
  .\build-deployment.ps1 -Help              # Show this help

Output Structure:
  deployment/
    ├── proxchattk v0.1.0/       # Portable app folder
    ├── proxchattk v0.1.0.zip    # Distribution zip
    ├── proxchattk v0.1.0.nupkg  # Update package
    └── releases.win.json        # Update index file

Temporary files (cleaned up automatically):
  deployment/temp/               # Temporary build files

Deployment:
  • Upload releases.win.json + .nupkg to your update server
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
    
    if (Test-Path $TempOutput) {
        Remove-Item -Recurse -Force $TempOutput
        Write-Host "Removed temp output directory: $TempOutput"
    }
    
    if (Test-Path ".\deployment\temp") {
        Remove-Item -Recurse -Force ".\deployment\temp"
        Write-Host "Removed deployment temp directory"
    }
}

function Build-For-Release {
    Write-Host "Building optimized release version..." -ForegroundColor Green
    Write-Host "This may take a few minutes due to optimization..." -ForegroundColor Yellow
    
    if (-not (Test-Path $TempOutput)) {
        New-Item -ItemType Directory -Path $TempOutput -Force | Out-Null
    }
    
    $publishArgs = @(
        "publish",
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "false",
        "--output", $TempOutput
    )
    
    # trimming is disabled by default for WPF apps (not recommended/supported)
    Write-Host "Trimming disabled - recommended for WPF apps" -ForegroundColor Cyan
    
    if ($Verbose) { 
        $publishArgs += "--verbosity", "detailed" 
    }
    
    & dotnet $publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed"
    }
    
    $exePath = Join-Path $TempOutput "ProxChatClient.exe"
    
    if (Test-Path $exePath) {
        $fileInfo = Get-Item $exePath
        $fileSizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
        
        Write-Host ""
        Write-Host "✅ Release build completed successfully!" -ForegroundColor Green
        Write-Host "📁 Location: $exePath" -ForegroundColor Cyan
        Write-Host "📊 File size: $fileSizeMB MB" -ForegroundColor Cyan
        Write-Host "📦 Framework-dependent build - requires .NET 9 runtime" -ForegroundColor Yellow
        Write-Host "🚀 Single-file executable ready!" -ForegroundColor Green
        
        # config files are NOT copied to output directory
        # they will be added to distribution packages only (not update packages)
        
        return $exePath
    } else {
        throw "Build succeeded but executable not found at expected location: $exePath"
    }
}

function Get-ProjectVersion {
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
    
    $vpkPath = Get-Command "vpk" -ErrorAction SilentlyContinue
    if (-not $vpkPath) {
        Write-Host "Installing Velopack tools..." -ForegroundColor Yellow
        & dotnet tool install -g vpk
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to install Velopack tools"
        }
    }
    
    $tempReleasesDir = ".\deployment\temp\vpk"
    if (Test-Path $tempReleasesDir) {
        Remove-Item $tempReleasesDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $tempReleasesDir -Force | Out-Null
    
    $vpkArgs = @(
        "pack",
        "--packId", "ProxChatTK",
        "--packVersion", $Version,
        "--packDir", $PublishPath,
        "--outputDir", $tempReleasesDir,
        "--mainExe", "ProxChatClient.exe"
    )
    
    Write-Host "Running: vpk $($vpkArgs -join ' ')" -ForegroundColor Cyan
    $vpkOutput = & vpk $vpkArgs 2>&1
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "VPK Error Output: $vpkOutput" -ForegroundColor Red
        throw "Velopack packaging failed"
    }
    
    return $tempReleasesDir
}

function Organize-Artifacts {
    param($PublishPath, $TempReleasesPath, $Version)
    
    Write-Host "Organizing deployment artifacts..." -ForegroundColor Green
    
    $deploymentDir = ".\deployment"
    if (Test-Path $deploymentDir) {
        # only remove non-temp files to preserve temp directory structure
        Get-ChildItem $deploymentDir | Where-Object { $_.Name -ne "temp" } | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Path $deploymentDir -Force | Out-Null
    }
    
    $portableZipPath = Join-Path $TempReleasesPath "*-Portable.zip"
    $portableZipFiles = Get-ChildItem -Path $portableZipPath -ErrorAction SilentlyContinue
    if (-not $portableZipFiles -or $portableZipFiles.Count -eq 0) {
        throw "Velopack portable package not found in $TempReleasesPath"
    }
    
    $versionedAppDir = Join-Path $deploymentDir "proxchattk v$Version"
    $proxChatTKDir = Join-Path $versionedAppDir "ProxChatTK"
    
    # create directories
    New-Item -ItemType Directory -Path $versionedAppDir -Force | Out-Null
    New-Item -ItemType Directory -Path $proxChatTKDir -Force | Out-Null
    
    # extract the portable package into the ProxChatTK subfolder
    Write-Host "Extracting Velopack portable package..." -ForegroundColor Green
    $portableZipFile = $portableZipFiles[0]
    Expand-Archive -Path $portableZipFile.FullName -DestinationPath $proxChatTKDir -Force
    Write-Host "  ✓ Extracted Velopack app to ProxChatTK subfolder" -ForegroundColor Cyan
    
    # add distribution-only files to the root level (not in update packages)
    Write-Host "Adding distribution-only files to root level..." -ForegroundColor Green
    
    # add VERSION.dll from memoryreadingdll build (root level)
    $versionDllPath = "..\memoryreadingdll\build\Release\VERSION.dll"
    if (Test-Path $versionDllPath) {
        $targetVersionDll = Join-Path $versionedAppDir "VERSION.dll"
        Copy-Item $versionDllPath $targetVersionDll -Force
        $dllSize = [math]::Round((Get-Item $targetVersionDll).Length / 1KB, 2)
        Write-Host "  ✓ VERSION.dll ($dllSize KB) - for game directory (root level)" -ForegroundColor Cyan
    } else {
        Write-Host "  ⚠️  VERSION.dll not found at: $versionDllPath" -ForegroundColor Yellow
        Write-Host "     Run the main build_package.ps1 script first to build the DLL" -ForegroundColor Yellow
    }
    
    # add user guide (root level)
    if (Test-Path "user_guide.txt") {
        $targetUserGuide = Join-Path $versionedAppDir "user_guide.txt"
        Copy-Item "user_guide.txt" $targetUserGuide -Force
        Write-Host "  ✓ user_guide.txt - usage instructions (root level)" -ForegroundColor Cyan
    } else {
        Write-Host "  ⚠️  user_guide.txt not found" -ForegroundColor Yellow
    }
    
    # add readme files if they exist (root level)
    $readmeFiles = @("DISTRIBUTION-README.md", "CONFIG-README.md")
    foreach ($readme in $readmeFiles) {
        if (Test-Path $readme) {
            $targetReadme = Join-Path $versionedAppDir $readme
            Copy-Item $readme $targetReadme -Force
            Write-Host "  ✓ $readme (root level)" -ForegroundColor Cyan
        }
    }
    
    # add config.json from config.json.default (inside ProxChatTK folder, persistent across updates)
    if (Test-Path "config.json.default") {
        $targetConfig = Join-Path $proxChatTKDir "config.json"
        Copy-Item "config.json.default" $targetConfig -Force
        Write-Host "  ✓ config.json (from config.json.default) - persistent across updates" -ForegroundColor Cyan
    } else {
        Write-Host "  ⚠️  config.json.default not found" -ForegroundColor Yellow
    }
    
    # add attributions file (inside ProxChatTK folder)
    if (Test-Path "ATTRIBUTIONS.txt") {
        $targetAttributions = Join-Path $proxChatTKDir "ATTRIBUTIONS.txt"
        Copy-Item "ATTRIBUTIONS.txt" $targetAttributions -Force
        Write-Host "  ✓ ATTRIBUTIONS.txt - third-party license information (ProxChatTK folder)" -ForegroundColor Cyan
    } else {
        Write-Host "  ⚠️  ATTRIBUTIONS.txt not found" -ForegroundColor Yellow
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
    
    # move nupkg file (contains only core app files) - keep original Velopack name
    $nupkgPath = Join-Path $TempReleasesPath "*.nupkg"
    $nupkgFiles = Get-ChildItem -Path $nupkgPath -ErrorAction SilentlyContinue
    if ($nupkgFiles -and $nupkgFiles.Count -gt 0) {
        $sourceNupkg = $nupkgFiles[0]
        $targetNupkg = Join-Path $deploymentDir $sourceNupkg.Name
        Copy-Item $sourceNupkg.FullName $targetNupkg -Force
        Write-Host "  ✓ Update package ($($sourceNupkg.Name)) - core app files only" -ForegroundColor Cyan
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
    
    # cleanup temp directories at the end (keep them during build for debugging)
    # Note: temp files are cleaned up automatically during next build or with -Clean flag
    
    return $deploymentDir
}

function Create-Shortcut {
    param($DeploymentPath, $Version)
    
    Write-Host "Creating launcher with --log proxchat argument..." -ForegroundColor Green
    
    $versionedAppDir = Join-Path $DeploymentPath "proxchattk v$Version"
    $proxChatTKDir = Join-Path $versionedAppDir "ProxChatTK"
    
    # target executable path - exactly as specified
    $targetExe = Join-Path $proxChatTKDir "ProxChatTK.exe"
    
    # batch file launcher path
    $batchPath = Join-Path $proxChatTKDir "ProxChatTK - with logging.bat"
    
    if (Test-Path $targetExe) {
        try {
            # create batch file content
            $batchContent = @"
@echo off
cd /d "%~dp0"
start "" "ProxChatTK.exe" --log proxchat
"@
            
            # write batch file
            Set-Content -Path $batchPath -Value $batchContent -Encoding ASCII
            
            Write-Host "  ✓ Created launcher: ProxChatTK - with logging.bat" -ForegroundColor Cyan
            Write-Host "  → Target: ProxChatTK.exe" -ForegroundColor Gray
            Write-Host "  → Arguments: --log proxchat" -ForegroundColor Gray
            
        } catch {
            Write-Host "  ⚠️  Failed to create launcher: $_" -ForegroundColor Yellow
        }
    } else {
        Write-Host "  ⚠️  Target executable not found: $targetExe" -ForegroundColor Yellow
    }
}

function Show-Results {
    param($DeploymentPath, $Version)
    
    Write-Host ""
    Write-Host "✅ Deployment artifacts created successfully!" -ForegroundColor Green
    Write-Host ""
    
    $versionedAppDir = Join-Path $DeploymentPath "proxchattk v$Version"
    $zipPath = Join-Path $DeploymentPath "proxchattk v$Version.zip"
    $nupkgPath = Get-ChildItem -Path (Join-Path $DeploymentPath "*.nupkg") -ErrorAction SilentlyContinue | Select-Object -First 1
    $releasesPath = Join-Path $DeploymentPath "releases.win.json"
    
    # show portable app folder structure
    if (Test-Path $versionedAppDir) {
        Write-Host "📁 Distribution: proxchattk v$Version/" -ForegroundColor Cyan
        
        # show root level files
        $versionDll = Join-Path $versionedAppDir "VERSION.dll"
        $userGuide = Join-Path $versionedAppDir "user_guide.txt"
        
        if (Test-Path $versionDll) {
            $dllSize = [math]::Round((Get-Item $versionDll).Length / 1KB, 2)
            Write-Host "📄 VERSION.dll ($dllSize KB) - copy to game directory" -ForegroundColor Cyan
        }
        
        if (Test-Path $userGuide) {
            Write-Host "📄 user_guide.txt - setup instructions" -ForegroundColor Cyan
        }
        
        # show ProxChatTK app subfolder
        $proxChatTKDir = Join-Path $versionedAppDir "ProxChatTK"
        if (Test-Path $proxChatTKDir) {
            Write-Host "📂 ProxChatTK/ - Velopack app folder" -ForegroundColor Cyan
            
            # check for stub launcher in subfolder
            $stubPath = Join-Path $proxChatTKDir "Update.exe"
            $currentDir = Join-Path $proxChatTKDir "current"
            $exePath = Join-Path $currentDir "ProxChatClient.exe"
            $configFile = Join-Path $proxChatTKDir "config.json"
            
            if (Test-Path $stubPath) {
                Write-Host "🚀 ProxChatTK/Update.exe (stub launcher) - users run this" -ForegroundColor Green
                
                if (Test-Path $exePath) {
                    $exeInfo = Get-Item $exePath
                    Write-Host "📊 Main app size: $([math]::Round($exeInfo.Length / 1MB, 2)) MB (in current/ folder)" -ForegroundColor Cyan
                }
            } else {
                # fallback for non-velopack structure
                $directExePath = Join-Path $proxChatTKDir "ProxChatClient.exe"
                if (Test-Path $directExePath) {
                    $exeInfo = Get-Item $directExePath
                    Write-Host "📊 Main exe size: $([math]::Round($exeInfo.Length / 1MB, 2)) MB" -ForegroundColor Cyan
                }
            }
            
            if (Test-Path $configFile) {
                Write-Host "📄 ProxChatTK/config.json - persistent across updates" -ForegroundColor Cyan
            }
            
            $attributionsFile = Join-Path $proxChatTKDir "ATTRIBUTIONS.txt"
            if (Test-Path $attributionsFile) {
                Write-Host "📄 ProxChatTK/ATTRIBUTIONS.txt - third-party license information" -ForegroundColor Cyan
            }
            
            # check for launcher
            $batchPath = Join-Path $proxChatTKDir "ProxChatTK - with logging.bat"
            if (Test-Path $batchPath) {
                Write-Host "🚀 ProxChatTK/ProxChatTK - with logging.bat - launcher with --log proxchat" -ForegroundColor Green
            }
        }
    }
    
    # show zip
    if (Test-Path $zipPath) {
        $zipInfo = Get-Item $zipPath
        Write-Host "📦 Distribution zip: proxchattk v$Version.zip" -ForegroundColor Cyan
        Write-Host "📊 Zip size: $([math]::Round($zipInfo.Length / 1MB, 2)) MB" -ForegroundColor Cyan
        Write-Host "   → Contains: VERSION.dll + user guide + ProxChatTK/ (with Update.exe stub + app + attributions)" -ForegroundColor Gray
    }
    
    # show update package
    if ($nupkgPath -and (Test-Path $nupkgPath.FullName)) {
        Write-Host "📦 Update package: $($nupkgPath.Name)" -ForegroundColor Cyan
        Write-Host "📊 Package size: $([math]::Round($nupkgPath.Length / 1MB, 2)) MB" -ForegroundColor Cyan
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
    Write-Host "• Upload releases.win.json + .nupkg to your update server" -ForegroundColor White
    Write-Host "• Distribute .zip or app folder to users" -ForegroundColor White
    Write-Host "• Users copy VERSION.dll to their game directory" -ForegroundColor White
    Write-Host "• Batch launcher created with --log proxchat argument for easy debugging" -ForegroundColor White
}

try {
    # Show help if explicitly requested
    if ($Help) {
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
    $releasesDir = Build-VelopackPackage -PublishPath $TempOutput -Version $version
    $deploymentDir = Organize-Artifacts -PublishPath $TempOutput -TempReleasesPath $releasesDir -Version $version
    Create-Shortcut -DeploymentPath $deploymentDir -Version $version
    Show-Results -DeploymentPath $deploymentDir -Version $version
    
} catch {
    Write-Host "Build failed: $_" -ForegroundColor Red
    exit 1
} 