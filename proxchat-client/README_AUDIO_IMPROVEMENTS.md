# Audio File Improvements

## New Features

### 1. File Browser for Audio Input

- **Browse Button**: Click "Browse..." to open a file dialog and select any audio file
- **Supported Formats**: WAV, MP3, M4A, AAC, WMA, FLAC
- **Real-time Display**: Shows selected file name in the UI
- **Fallback**: If no file is selected, uses the default `test.wav` file

### 2. Real-time MP3 Transcoding

- **MP3 Support**: Can now play MP3 files directly into the broadcast stream
- **MediaFoundation**: Uses Windows MediaFoundation for MP3 decoding
- **Real-time Conversion**: Converts MP3 to MuLaw (PCMU) format in real-time
- **No Pre-processing**: No need to convert files beforehand

### 3. Enhanced Audio Processing

- **Format Detection**: Automatically detects audio file format
- **Universal Support**: Handles various audio formats through MediaFoundation
- **Quality Preservation**: Maintains audio quality during real-time conversion
- **Error Handling**: Graceful fallback to silence if file cannot be processed

## How to Use

### Basic Usage

1. Enable "Use Audio File" checkbox in Debug Controls
2. Click "Browse..." button
3. Select any supported audio file (MP3, WAV, etc.)
4. The file will immediately start playing into the broadcast stream

### Supported File Formats

- **WAV**: Native support, best performance
- **MP3**: Real-time transcoding via MediaFoundation
- **M4A**: iTunes/Apple format support
- **AAC**: Advanced Audio Codec
- **WMA**: Windows Media Audio
- **FLAC**: Lossless audio format

### Technical Details

- **Sample Rate**: All formats converted to 8kHz for WebRTC compatibility
- **Encoding**: Final output is MuLaw (PCMU) for WebRTC transmission
- **Channels**: Converted to mono for voice chat optimization
- **Looping**: Audio files automatically loop when they reach the end

## Implementation Notes

### MediaFoundation Initialization

- Automatically initialized in AudioService static constructor
- Graceful fallback if MediaFoundation is not available
- No additional setup required

### Performance Considerations

- Real-time transcoding has minimal CPU impact
- Circular buffering prevents audio dropouts
- Efficient memory usage with streaming conversion

### Error Handling

- Invalid files gracefully fall back to silence
- File not found errors are logged and handled
- Unsupported formats attempt MediaFoundation fallback

## Troubleshooting

### File Not Playing

1. Check if file exists and is accessible
2. Verify file format is supported
3. Check debug logs for specific error messages
4. Try a different audio file

### Poor Audio Quality

1. Use higher quality source files
2. WAV files provide best quality
3. Avoid heavily compressed MP3 files
4. Check input volume settings

### Performance Issues

1. Use shorter audio files for testing
2. Close other audio applications
3. Check system audio device settings
4. Monitor CPU usage during playback
