# Build script for prox-chat-server with multi-architecture support

param(
    [switch]$Docker,
    [switch]$MultiArch,
    [switch]$Linux,
    [switch]$DockerExtract,
    [switch]$Run,
    [switch]$Push,
    [string]$Tag = "latest",
    [string]$Registry = "", # Container registry URL for pushing images
    [string]$Platform = "linux/amd64", # Default to x86 for local development
    [string]$Output = ".\dist"
)

$ErrorActionPreference = "Stop"

function Build-Local {
    Write-Host "Building locally with Cargo..."
    cargo build --release
    if ($LASTEXITCODE -ne 0) {
        throw "Cargo build failed"
    }
}

function Build-Linux {
    Write-Host "Building Linux binary via cross-compilation..."
    
    # Ensure output directory exists
    if (-not (Test-Path $Output)) {
        New-Item -ItemType Directory -Path $Output -Force | Out-Null
    }
    
    # Check if Linux target is installed
    Write-Host "Checking for Linux target..."
    $targets = rustup target list --installed
    if ($targets -notcontains "x86_64-unknown-linux-gnu") {
        Write-Host "Installing Linux target..." -ForegroundColor Yellow
        rustup target add x86_64-unknown-linux-gnu
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to install Linux target"
        }
    }
    
    # Build for Linux
    Write-Host "Cross-compiling for x86_64-unknown-linux-gnu..."
    cargo build --release --target x86_64-unknown-linux-gnu
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "❌ Linux cross-compilation failed!" -ForegroundColor Red
        Write-Host ""
        Write-Host "This is likely due to missing Linux toolchain (linker 'cc' not found)." -ForegroundColor Yellow
        Write-Host "Cross-compilation from Windows requires additional setup." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "🚀 Alternative options:" -ForegroundColor Green
        Write-Host "1. Use Docker extract (RECOMMENDED):" -ForegroundColor Cyan
        Write-Host "   .\build.ps1 -DockerExtract" -ForegroundColor Gray
        Write-Host ""
        Write-Host "2. Build directly on your VPS:" -ForegroundColor Cyan
        Write-Host "   # Upload source code and build there" -ForegroundColor Gray
        Write-Host ""
        Write-Host "📖 See VPS-DEPLOYMENT-GUIDE.md for complete instructions" -ForegroundColor Green
        throw "Linux cross-compilation failed"
    }
    
    # Copy binary to output directory
    $sourceBinary = "target\x86_64-unknown-linux-gnu\release\prox-chat-server"
    $targetBinary = Join-Path $Output "prox-chat-server-linux"
    
    if (-not (Test-Path $sourceBinary)) {
        throw "Linux binary not found at: $sourceBinary"
    }
    
    Copy-Item $sourceBinary $targetBinary -Force
    
    $binarySize = [math]::Round((Get-Item $targetBinary).Length / 1MB, 2)
    Write-Host ""
    Write-Host "✅ Linux binary built successfully!" -ForegroundColor Green
    Write-Host "📁 Location: $targetBinary" -ForegroundColor Cyan
    Write-Host "📊 Size: $binarySize MB" -ForegroundColor Cyan
    Write-Host "🐧 Target: x86_64-unknown-linux-gnu" -ForegroundColor Cyan
    Write-Host "📦 Ready for VPS deployment!" -ForegroundColor Green
    Write-Host ""
    Write-Host "📖 Next steps: See VPS-DEPLOYMENT-GUIDE.md" -ForegroundColor Yellow
    
    return $targetBinary
}

function Build-Docker-Single {
    param($ImageName, $Platform)
    Write-Host "Building Docker image for platform: $Platform"
    docker buildx build --platform $Platform -t $ImageName .
    if ($LASTEXITCODE -ne 0) {
        throw "Docker build failed"
    }
}

function Build-Docker-MultiArch {
    param($ImageName, $ShouldPush)
    Write-Host "Building multi-architecture Docker image (linux/amd64,linux/arm64)..."
    
    # Create and use a new builder instance that supports multi-arch
    $builderName = "prox-chat-multiarch"
    docker buildx create --name $builderName --use --bootstrap 2>$null
    
    $pushFlag = if ($ShouldPush) { "--push" } else { "--load" }
    
    if ($ShouldPush) {
        docker buildx build --platform linux/amd64,linux/arm64 -t $ImageName --push .
    } else {
        # For local multi-arch builds without pushing, we can't use --load with multiple platforms
        # So we'll build and keep in buildx cache
        Write-Host "Building multi-arch image (cached only - use --Push to publish)"
        docker buildx build --platform linux/amd64,linux/arm64 -t $ImageName .
    }
    
    if ($LASTEXITCODE -ne 0) {
        throw "Docker multi-arch build failed"
    }
}

function Setup-Buildx {
    Write-Host "Setting up Docker buildx for multi-architecture builds..."
    
    # Check if our builder already exists
    $builderExists = docker buildx ls | Select-String "prox-chat-multiarch"
    
    if (-not $builderExists) {
        Write-Host "Creating new buildx builder..."
        docker buildx create --name prox-chat-multiarch --use --bootstrap
    } else {
        Write-Host "Using existing buildx builder..."
        docker buildx use prox-chat-multiarch
    }
}

function Run-Local {
    Write-Host "Running local build..."
    cargo run --release
}

function Run-Docker {
    param($ImageName)
    Write-Host "Running Docker container..."
    docker run -p 8080:8080 $ImageName
}

function Build-DockerExtract {
    param($ImageName, $OutputPath)
    
    Write-Host "Building Docker image and extracting Linux binary..." -ForegroundColor Yellow
    
    # Build Docker image first
    Build-Docker-Single -ImageName $ImageName -Platform "linux/amd64"
    
    # Extract binary from the image
    Write-Host ""
    Write-Host "Extracting Linux binary from Docker image..." -ForegroundColor Yellow
    Write-Host "Image: $ImageName" -ForegroundColor Cyan
    
    # Ensure output directory exists
    if (-not (Test-Path $OutputPath)) {
        New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
    }
    
    # Create temporary container
    Write-Host "Creating temporary container..." -ForegroundColor Gray
    $containerId = docker create $ImageName
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create container from image: $ImageName"
    }
    
    try {
        # Extract binary
        $targetBinary = Join-Path $OutputPath "prox-chat-server-linux"
        Write-Host "Extracting binary..." -ForegroundColor Gray
        docker cp "${containerId}:/usr/local/bin/prox-chat-server" $targetBinary
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to extract binary from container"
        }
        
        # Check if binary was extracted
        if (-not (Test-Path $targetBinary)) {
            throw "Binary not found after extraction: $targetBinary"
        }
        
        $binarySize = [math]::Round((Get-Item $targetBinary).Length / 1MB, 2)
        Write-Host ""
        Write-Host "✅ Linux binary extracted successfully!" -ForegroundColor Green
        Write-Host "📁 Location: $targetBinary" -ForegroundColor Cyan
        Write-Host "📊 Size: $binarySize MB" -ForegroundColor Cyan
        Write-Host "🐧 Target: x86_64-unknown-linux-gnu" -ForegroundColor Cyan
        Write-Host "📦 Ready for VPS deployment!" -ForegroundColor Green
        Write-Host ""
        Write-Host "📖 Next steps: See VPS-DEPLOYMENT-GUIDE.md" -ForegroundColor Yellow
        
        return $targetBinary
        
    } finally {
        # Clean up temporary container
        Write-Host "Cleaning up temporary container..." -ForegroundColor Gray
        docker rm $containerId | Out-Null
    }
}

function Show-Help {
    Write-Host @"
Prox-Chat Server Build Script

Usage:
  .\build.ps1 [options]

Options:
  -Docker          Build with Docker instead of local Cargo
  -MultiArch       Build multi-architecture image (linux/amd64,linux/arm64)
  -Linux           Build Linux binary via cross-compilation
  -DockerExtract   Build Docker image and extract Linux binary for VPS deployment
  -Run             Run the application after building
  -Push            Push image to registry (requires -Registry)
  -Tag <tag>       Docker image tag (default: latest)
  -Registry <url>  Container registry URL (for pushing)
  -Platform <plat> Platform for single-arch Docker builds (default: linux/amd64)
  -Output <path>   Output directory for Linux binary (default: .\dist)

Examples:
  .\build.ps1                                    # Local Cargo build
  .\build.ps1 -Docker -Run                      # Docker build and run locally
  .\build.ps1 -Linux                            # Cross-compile for Linux VPS
  .\build.ps1 -DockerExtract                    # Build Docker + extract Linux binary for VPS  
  .\build.ps1 -Docker -MultiArch                # Multi-arch build (cached)
  .\build.ps1 -Docker -MultiArch -Push -Registry "your-ecr-repo" -Tag "v1.0"

VPS Deployment:
  .\build.ps1 -DockerExtract                    # Build and extract Linux binary (RECOMMENDED)
  .\build.ps1 -Linux                            # Cross-compile (may need WSL/toolchain)
  # Then upload prox-chat-server-linux to your VPS

AWS ECR Example:
  # Login to ECR first:
  aws ecr get-login-password --region us-east-1 | docker login --username AWS --password-stdin 123456789012.dkr.ecr.us-east-1.amazonaws.com
  
  # Build and push:
  .\build.ps1 -Docker -MultiArch -Push -Registry "123456789012.dkr.ecr.us-east-1.amazonaws.com/prox-chat-server" -Tag "latest"
"@
}

try {
    # Show help if no parameters
    if (-not ($Docker -or $Linux -or $DockerExtract -or $PSBoundParameters.Count -gt 0)) {
        Show-Help
        return
    }

    # Determine image name
    $imageName = if ($Registry) { "${Registry}:${Tag}" } else { "prox-chat-server:${Tag}" }

    if ($Linux) {
        Build-Linux
    } elseif ($DockerExtract) {
        Build-DockerExtract -ImageName $imageName -OutputPath $Output
    } elseif ($Docker) {
        if ($MultiArch) {
            Setup-Buildx
            Build-Docker-MultiArch -ImageName $imageName -ShouldPush $Push
            
            if ($Run -and -not $Push) {
                Write-Host "Note: Cannot run multi-arch cached image directly. Building single platform for local run..."
                $localImageName = "prox-chat-server:local-run"
                Build-Docker-Single -ImageName $localImageName -Platform "linux/amd64"
                Run-Docker -ImageName $localImageName
            }
        } else {
            Build-Docker-Single -ImageName $imageName -Platform $Platform
            if ($Push) {
                Write-Host "Pushing image to registry..."
                docker push $imageName
            }
            if ($Run) {
                Run-Docker -ImageName $imageName
            }
        }
    } else {
        Build-Local
        if ($Run) {
            Run-Local
        }
    }
    
    Write-Host "Build completed successfully!" -ForegroundColor Green
    
    if ($MultiArch -and -not $Push -and $Docker) {
        Write-Host ""
        Write-Host "Multi-arch image built and cached. To deploy to AWS ECS:" -ForegroundColor Yellow
        Write-Host "1. See AWS-deployment-guide.md for complete deployment instructions" -ForegroundColor Yellow  
        Write-Host "2. Or run: .\build.ps1 -Docker -MultiArch -Push -Registry <your-ecr-repo>" -ForegroundColor Yellow
    }
    
} catch {
    Write-Host "Build failed: $_" -ForegroundColor Red
    exit 1
} 