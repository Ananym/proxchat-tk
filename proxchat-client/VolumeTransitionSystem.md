# Volume Transition System

## Overview

The volume transition system provides smooth linear interpolation between volume levels over 200ms, preventing jarring audio level jumps when peer volumes change due to distance, UI adjustments, or other factors.

## Key Features

- **Smooth Transitions**: Linear interpolation over 200ms duration
- **Resilient to Rapid Changes**: If volume target changes during transition, seamlessly updates to new target
- **Immediate Mode**: Option for instant volume changes when needed (e.g., muting)
- **Automatic Cleanup**: Handles peer disconnections and completed transitions

## How It Works

### Normal Volume Changes

When `AudioService.SetPeerUiVolume()` or distance-based volume updates occur:

1. System calculates new target volume
2. If change is significant (>0.001), starts smooth transition
3. Updates volume every 10ms using linear interpolation
4. Completes transition after 200ms

### Rapid Volume Changes

If volume target changes during active transition:

1. Calculates current interpolated volume as new starting point
2. Sets new target volume
3. Restarts 200ms transition from current position
4. No jarring jumps or interruptions

### Immediate Changes

For cases requiring instant feedback (muting/unmuting):

- `SetVolumeImmediate()` bypasses transition system
- Cancels any active transition
- Applies volume instantly

## Usage Examples

### Standard Volume Change (Smooth)

```csharp
// This will smoothly transition over 200ms
audioService.SetPeerUiVolume("peer123", 0.8f);
```

### Immediate Volume Change

```csharp
// This applies instantly (useful for muting)
audioService.SetPeerUiVolumeImmediate("peer123", 0.0f);
```

### Muting (Automatic Immediate)

```csharp
// Muting uses immediate change for instant feedback
audioService.SetPeerMuteState("peer123", true);

// Unmuting uses smooth transition
audioService.SetPeerMuteState("peer123", false);
```

## Configuration

Current settings in `VolumeTransitionService.cs`:

- `TRANSITION_DURATION_MS = 200` - Total transition time
- `UPDATE_INTERVAL_MS = 10` - Update frequency (100 FPS)
- `MIN_VOLUME_CHANGE = 0.001f` - Minimum change to trigger transition

## Performance

- Minimal CPU overhead: Only processes active transitions
- Memory efficient: Cleans up completed transitions automatically
- Thread-safe: Uses concurrent collections and locking

## Testing Scenarios

1. **Single Volume Change**: Smooth 200ms transition
2. **Rapid Changes**: Multiple volume changes during transition
3. **Distance-Based**: Volume changes as peers move
4. **Muting/Unmuting**: Instant mute, smooth unmute
5. **Peer Disconnection**: Proper cleanup of transitions

## Benefits

- **Better UX**: No jarring volume jumps
- **Professional Feel**: Smooth audio transitions like modern voice apps
- **Resilient**: Handles edge cases gracefully
- **Configurable**: Easy to adjust timing and behavior
