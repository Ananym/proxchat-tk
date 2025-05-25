# Extract Linux binary from Docker image for VPS deployment

param(
    [string]$ImageName = "prox-chat-server:latest",
    [string]$Output = ".\dist"
)

$ErrorActionPreference = "Stop"

function Extract-Binary {
    param($ImageName, $OutputPath)
    
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
Extract Linux Binary from Docker Image

Usage:
  .\extract-binary.ps1 [options]

Options:
  -ImageName <name>  Docker image name (default: prox-chat-server:latest)
  -Output <path>     Output directory (default: .\dist)

Examples:
  .\extract-binary.ps1                                   # Extract from latest image
  .\extract-binary.ps1 -ImageName "prox-chat-server:v1.0" # Extract from specific tag
  .\extract-binary.ps1 -Output "C:\temp"                 # Custom output directory

Prerequisites:
  1. Docker must be running
  2. Target image must exist (build with: .\build.ps1 -Docker)

Output:
  Creates: prox-chat-server-linux (executable for VPS deployment)
"@
}

# Main execution
try {
    if (-not $PSBoundParameters.Count) {
        Show-Help
        return
    }
    
    # Check if Docker is running
    docker version | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Docker is not running or not installed"
    }
    
    # Check if image exists
    $imageExists = docker images $ImageName --format "{{.Repository}}:{{.Tag}}" | Select-String -Pattern $ImageName -Quiet
    if (-not $imageExists) {
        Write-Host "Image '$ImageName' not found. Available images:" -ForegroundColor Yellow
        docker images --format "table {{.Repository}}\t{{.Tag}}\t{{.Size}}"
        throw "Image not found: $ImageName"
    }
    
    Extract-Binary -ImageName $ImageName -OutputPath $Output
    
} catch {
    Write-Host "Extraction failed: $_" -ForegroundColor Red
    exit 1
} 