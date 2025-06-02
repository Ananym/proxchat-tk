#!/usr/bin/env pwsh

# test script for zeromq ipc communication
Write-Host "=== ProxChat ZeroMQ Test Script ===" -ForegroundColor Green

# paths - adjust game exe path if needed
$projectRoot = $PSScriptRoot
$clientLogPath = "$projectRoot\normalmode.log"
$dllLogPath = "E:\NexusTK\memoryreadingdll_log.txt"
$sourceDll = "$projectRoot\memoryreadingdll\build\Release\VERSION.dll"
$targetDll = "E:\NexusTK\VERSION.dll"
$clientExe = "$projectRoot\proxchat-client\bin\Debug\net9.0-windows10.0.17763.0\win-x64\ProxChatClient.exe"
$gameExe = "E:\NexusTK\NexusTK.exe"  # adjust this path if needed

Write-Host "Cleaning up old log files..." -ForegroundColor Yellow

# delete normalmode.log if it exists
if (Test-Path $clientLogPath) {
    Remove-Item $clientLogPath -Force
    Write-Host "  Deleted: $clientLogPath"
} else {
    Write-Host "  normalmode.log not found (ok)"
}

# delete dll log if it exists  
if (Test-Path $dllLogPath) {
    "" | Out-File $dllLogPath -Encoding UTF8
    Write-Host "  Cleared: $dllLogPath"
} else {
    "" | Out-File $dllLogPath -Encoding UTF8
    Write-Host "  Created: $dllLogPath"
}

Write-Host "Copying VERSION.dll to game directory..." -ForegroundColor Yellow

# copy built dll to game folder
if (Test-Path $sourceDll) {
    Copy-Item $sourceDll $targetDll -Force
    Write-Host "  Copied: $sourceDll -> $targetDll"
} else {
    Write-Host "  ERROR: Source DLL not found at $sourceDll" -ForegroundColor Red
    Write-Host "  Run build first: cd memoryreadingdll && .\build.ps1" -ForegroundColor Red
    exit 1
}

Write-Host "Starting ProxChat client..." -ForegroundColor Yellow

# start client with logging
if (Test-Path $clientExe) {
    $clientProcess = Start-Process -FilePath $clientExe -ArgumentList "--log", "normalmode" -PassThru -WindowStyle Normal
    Write-Host "  Client started (PID: $($clientProcess.Id))"
} else {
    Write-Host "  ERROR: Client exe not found at $clientExe" -ForegroundColor Red
    Write-Host "  Run build first: cd proxchat-client && dotnet build" -ForegroundColor Red
    exit 1
}

Write-Host "Waiting 3 seconds before starting game..." -ForegroundColor Yellow
Start-Sleep -Seconds 3

Write-Host "Starting NexusTK game..." -ForegroundColor Yellow

# start game
if (Test-Path $gameExe) {
    $gameProcess = Start-Process -FilePath $gameExe -PassThru -WindowStyle Normal
    Write-Host "  Game started (PID: $($gameProcess.Id))"
} else {
    Write-Host "  ERROR: Game exe not found at $gameExe" -ForegroundColor Red
    Write-Host "  Please adjust the `$gameExe path in this script" -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "=== Test Started Successfully ===" -ForegroundColor Green
Write-Host "Client PID: $($clientProcess.Id)" -ForegroundColor Cyan
Write-Host "Game PID: $($gameProcess.Id)" -ForegroundColor Cyan
Write-Host ""
Write-Host "To monitor logs in real-time:" -ForegroundColor Yellow
Write-Host "  Client: Get-Content '$clientLogPath' -Wait" -ForegroundColor Gray
Write-Host "  DLL:    Get-Content '$dllLogPath' -Wait" -ForegroundColor Gray
Write-Host ""
Write-Host "Press Ctrl+C to stop monitoring, or close windows manually" 