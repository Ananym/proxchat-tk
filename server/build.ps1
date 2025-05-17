# Build script for prox-chat-server

param(
    [switch]$Docker,
    [switch]$Run,
    [string]$Tag = "latest"
)

$ErrorActionPreference = "Stop"

function Build-Local {
    Write-Host "Building locally with Cargo..."
    cargo build --release
    if ($LASTEXITCODE -ne 0) {
        throw "Cargo build failed"
    }
}

function Build-Docker {
    Write-Host "Building Docker image..."
    docker build -t "prox-chat-server:$Tag" .
    if ($LASTEXITCODE -ne 0) {
        throw "Docker build failed"
    }
}

function Run-Local {
    Write-Host "Running local build..."
    cargo run --release
}

function Run-Docker {
    Write-Host "Running Docker container..."
    docker run -p 8080:8080 "prox-chat-server:$Tag"
}

try {
    if ($Docker) {
        Build-Docker
        if ($Run) {
            Run-Docker
        }
    } else {
        Build-Local
        if ($Run) {
            Run-Local
        }
    }
    Write-Host "Build completed successfully!"
} catch {
    Write-Host "Build failed: $_" -ForegroundColor Red
    exit 1
} 