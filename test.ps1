#!/usr/bin/env pwsh

param(
    [switch]$s = $false,   # run nexustk.exe (server only)
    [switch]$bs = $false,  # build dll + run nexustk.exe (build server)
    [switch]$n = $false,   # run proxchat client normal mode
    [switch]$d = $false,   # run proxchat client debug mode
    [switch]$b = $false    # build proxchat client only
)

# test script for zeromq ipc communication
Write-Host "=== ProxChat ZeroMQ Test Script ===" -ForegroundColor Green

if ($s -and $bs) {
    Write-Host "ERROR: -s and -bs are mutually exclusive" -ForegroundColor Red
    Write-Host "Use -s for server only, or -bs for build+server" -ForegroundColor Yellow
    exit 1
}

$actions = @()
if ($bs) { $actions += "build dll + server" }
elseif ($s) { $actions += "server" }
if ($b) { $actions += "build client" }
if ($n) { $actions += "client (normal)" }
if ($d) { $actions += "client (debug)" }

if ($actions.Count -eq 0) {
    Write-Host "No actions specified. Available options:" -ForegroundColor Yellow
    Write-Host "  -s   : run nexustk.exe (server only)" -ForegroundColor Gray
    Write-Host "  -bs  : build dll + run nexustk.exe (build server)" -ForegroundColor Gray
    Write-Host "  -n   : run proxchat client (normal mode)" -ForegroundColor Gray
    Write-Host "  -d   : run proxchat client (debug mode)" -ForegroundColor Gray
    Write-Host "  -b   : build proxchat client" -ForegroundColor Gray
    Write-Host "" -ForegroundColor Gray
    Write-Host "Examples:" -ForegroundColor Yellow
    Write-Host "  .\test.ps1 -s           # server only" -ForegroundColor Gray
    Write-Host "  .\test.ps1 -bs -n       # build dll, server, normal client" -ForegroundColor Gray
    Write-Host "  .\test.ps1 -b -n -d     # build client, run normal + debug" -ForegroundColor Gray
    Write-Host "  .\test.ps1 -bs -b -n -d # build all, run server + both clients" -ForegroundColor Gray
    exit 0
}

Write-Host "Actions: $($actions -join ', ')" -ForegroundColor Cyan
Write-Host ""

$projectRoot = $PSScriptRoot
$clientNormalLogPath = "$projectRoot\normalmode.log"
$clientDebugLogPath = "$projectRoot\debugmode.log"
$dllLogPath = "$projectRoot\memoryreadingdll_log.txt"
$sourceDll = "$projectRoot\memoryreadingdll\build\Release\VERSION.dll"
$targetDll = "E:\NexusTK\VERSION.dll"
$clientExe = "$projectRoot\proxchat-client\bin\Debug\net9.0-windows10.0.17763.0\win-x64\ProxChatClient.exe"
$gameExe = "E:\NexusTK\NexusTK.exe"  # adjust this path if needed

$startedProcesses = @()

# === BUILD PHASE ===

if ($bs) {
    Write-Host "Building memory reading DLL..." -ForegroundColor Yellow
    
    $originalDir = Get-Location
    try {
        Set-Location "$projectRoot\memoryreadingdll"
        & .\build.ps1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERROR: Memory reading DLL build failed" -ForegroundColor Red
            exit 1
        }
        Write-Host "Memory reading DLL build completed" -ForegroundColor Green
    }
    finally {
        Set-Location $originalDir
    }
    
    Write-Host "Copying VERSION.dll to game directory..." -ForegroundColor Yellow
    if (Test-Path $sourceDll) {
        Copy-Item $sourceDll $targetDll -Force
        Write-Host "Copied: $sourceDll -> $targetDll" -ForegroundColor Green
    } else {
        Write-Host "ERROR: Source DLL not found at $sourceDll" -ForegroundColor Red
        exit 1
    }
}

if ($b) {
    Write-Host "Building ProxChat client..." -ForegroundColor Yellow
    
    $originalDir = Get-Location
    try {
        Set-Location "$projectRoot\proxchat-client"
        & dotnet build
        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERROR: Client build failed" -ForegroundColor Red
            exit 1
        }
        Write-Host "Client build completed" -ForegroundColor Green
    }
    finally {
        Set-Location $originalDir
    }
}

# === CLEANUP PHASE ===

if ($s -or $bs) {
    Write-Host "Cleaning server log..." -ForegroundColor Yellow
    if (Test-Path $dllLogPath) {
        Remove-Item $dllLogPath -Force
        Write-Host "Deleted: $dllLogPath"
    }
}

if ($n) {
    Write-Host "Cleaning normal client log..." -ForegroundColor Yellow
    if (Test-Path $clientNormalLogPath) {
        Remove-Item $clientNormalLogPath -Force
        Write-Host "Deleted: $clientNormalLogPath"
    }
}

if ($d) {
    Write-Host "Cleaning debug client log..." -ForegroundColor Yellow
    if (Test-Path $clientDebugLogPath) {
        Remove-Item $clientDebugLogPath -Force
        Write-Host "Deleted: $clientDebugLogPath"
    }
}

# === EXECUTION PHASE ===

if ($s -or $bs) {
    Write-Host "Starting NexusTK server..." -ForegroundColor Yellow
    
    if (Test-Path $gameExe) {
        $gameProcess = Start-Process -FilePath $gameExe -ArgumentList "--debug" -PassThru -WindowStyle Normal
        Write-Host "Server started (PID: $($gameProcess.Id))" -ForegroundColor Green
        $startedProcesses += @{ Type = "Server"; PID = $gameProcess.Id }
    } else {
        Write-Host "ERROR: Game exe not found at $gameExe" -ForegroundColor Red
        Write-Host "Please adjust the game exe path" -ForegroundColor Yellow
        exit 1
    }
    
    if ($n -or $d) {
        Write-Host "Waiting 3 seconds before starting clients..." -ForegroundColor Yellow
        Start-Sleep -Seconds 3
    }
}

if ($n) {
    Write-Host "Starting ProxChat client (normal mode)..." -ForegroundColor Yellow
    
    if (Test-Path $clientExe) {
        $clientProcess = Start-Process -FilePath $clientExe -ArgumentList "--log", "normalmode" -PassThru -WindowStyle Normal
        Write-Host "Normal client started (PID: $($clientProcess.Id))" -ForegroundColor Green
        $startedProcesses += @{ Type = "Client (Normal)"; PID = $clientProcess.Id }
    } else {
        Write-Host "ERROR: Client exe not found at $clientExe" -ForegroundColor Red
        Write-Host "Run build first with -b option" -ForegroundColor Yellow
        exit 1
    }
}

if ($d) {
    Write-Host "Starting ProxChat client (debug mode)..." -ForegroundColor Yellow
    
    if (Test-Path $clientExe) {
        $clientProcess = Start-Process -FilePath $clientExe -ArgumentList "--debug", "--log", "debugmode" -PassThru -WindowStyle Normal
        Write-Host "Debug client started (PID: $($clientProcess.Id))" -ForegroundColor Green
        $startedProcesses += @{ Type = "Client (Debug)"; PID = $clientProcess.Id }
    } else {
        Write-Host "ERROR: Client exe not found at $clientExe" -ForegroundColor Red
        Write-Host "Run build first with -b option" -ForegroundColor Yellow
        exit 1
    }
}

# === SUMMARY ===

Write-Host ""
Write-Host "=== Started Successfully ===" -ForegroundColor Green

if ($startedProcesses.Count -gt 0) {
    foreach ($proc in $startedProcesses) {
        Write-Host "$($proc.Type): PID $($proc.PID)" -ForegroundColor Cyan
    }
} else {
    Write-Host "No processes started (build-only operation)" -ForegroundColor Cyan
}

Write-Host ""
Write-Host "To monitor logs in real-time:" -ForegroundColor Yellow
if ($s -or $bs) {
    Write-Host "  Server: Get-Content '$dllLogPath' -Wait" -ForegroundColor Gray
}
if ($n) {
    Write-Host "  Normal: Get-Content '$clientNormalLogPath' -Wait" -ForegroundColor Gray
}
if ($d) {
    Write-Host "  Debug:  Get-Content '$clientDebugLogPath' -Wait" -ForegroundColor Gray
}
Write-Host ""
Write-Host "Press Ctrl+C to stop monitoring, or close windows manually" 