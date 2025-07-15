# Convert audio file to 48kHz mono with high quality settings
# Usage: .\convert-to-48khz.ps1 "input-file.mp3" [output-file.wav]

param(
    [Parameter(Mandatory=$true)]
    [string]$InputFile,
    
    [Parameter(Mandatory=$false)]
    [string]$OutputFile
)

# Check if input file exists
if (-not (Test-Path $InputFile)) {
    Write-Error "Input file not found: $InputFile"
    exit 1
}

# Generate output filename if not provided
if (-not $OutputFile) {
    $inputName = [System.IO.Path]::GetFileNameWithoutExtension($InputFile)
    $OutputFile = "${inputName}_48khz_mono.mp3"
}

Write-Host "Converting $InputFile to 48kHz mono..."
Write-Host "Output: $OutputFile"

# High quality conversion settings:
# - 48kHz sample rate
# - Convert to mono with proper downmixing (L+R)/2
# - Normalize to prevent clipping
# - High quality resampling filter
# - MP3 format with good compression (192kbps)

try {
    ffmpeg -i $InputFile -ar 48000 -ac 1 -acodec libmp3lame -b:a 192k -af "aresample=48000:resampler=soxr:precision=28,pan=mono|c0=0.5*FL+0.5*FR,loudnorm=I=-16:TP=-1.5:LRA=11" -y $OutputFile
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Conversion successful!" -ForegroundColor Green
        Write-Host "File: $OutputFile"
        
        # Show file info
        Write-Host "`nFile details:"
        ffprobe -v quiet -select_streams a:0 -show_entries stream=sample_rate,channels,bit_rate -of csv=p=0 $OutputFile
    } else {
        Write-Error "Conversion failed with exit code: $LASTEXITCODE"
        exit 1
    }
}
catch {
    Write-Error "Error during conversion: $($_.Exception.Message)"
    exit 1
} 