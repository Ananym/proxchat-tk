# WebRTC Audio Debugging Summary

## Original Problem

WebRTC connection established successfully between two clients (data channel for position updates works), but audio sent by one client was not received by the other. This was tested with both clients on the same machine.

## Investigation and Learnings

- **Initial State:**

  - WebRTC connection (including ICE and SDP exchange) and data channels were confirmed to be working.
  - Logs showed audio data being _captured_ and the sending client _attempting_ to send audio packets.
  - Crucially, no log lines on the receiving client indicated that RTP packets were being received or processed (e.g., no `Received RTP packet from ...` or `Processed audio data from ...` in `WebRtcService`, and `AudioService.PlayAudio` was not being hit with peer audio).

- **Key Findings During Debugging:**
  1.  **Incorrect Audio Sending Mechanism:** `WebRtcService` was attempting to send audio via `_audioEndPoint.GotAudioRtp()`, which is for local processing of _received_ packets.
  2.  **Improper Audio Packetization & Encoding:** `AudioService` produced large audio chunks not suitable for WebRTC and didn't consistently resample/encode to 8kHz PCMU.
  3.  **Audio Playback Misconfiguration:** `AudioService.CreatePeerPlayback` used an incorrect `WaveFormat` for its buffer, and `PlayAudio` didn't decode incoming PCMU.
  4.  **Terminology Cleanup:** References to "MP3" were updated to "Audio File" as WAV is now used for testing.
  5.  **Logging Verbosity:** Initial logging was too dense for effective analysis.
  6.  **SSRC Association for Received Audio:** The mechanism to link incoming RTP packets (via SSRC) to the correct peer and then to `AudioService.PlayAudio` was missing. The `OnTrack` event handler caused build issues with the current SIPSorcery library version.
  7.  **`test.wav` Format:** The primary test audio file's format needed confirmation.

## Changes Implemented

- **`WebRtcService.cs`:**

  - **Audio Sending:**
    - Modified `OnEncodedAudioPacketReadyToSend` to use `state.PeerConnection.SendAudio((uint)samplesInPacket, audioData)` for correct audio transmission.
    - Removed locally managed RTP sequence numbers and adjusted timestamp logic.
  - **Audio Receiving & SSRC Handling:**
    - Removed the problematic `OnTrack` event handler.
    - **SSRC Association:** Implemented logic in `OnOfferReceived` and `OnAnswerReceived` to attempt to capture the remote audio SSRC from `pc.AudioRemoteTrack.Ssrc` after `setRemoteDescription` is successfully called. This SSRC is stored in `PeerConnectionState.RemoteAudioSsrc`.
    - Modified `OnRtpPacketReceived`:
      - It now uses the `rtpPacket.Header.SyncSource` (SSRC) to find the corresponding `peerId` by looking up `RemoteAudioSsrc` in the `_peerConnections` dictionary.
      - If a `peerId` is found, it calls `_audioService.PlayAudio(foundPeerId, rtpPacket.Payload, rtpPacket.Payload.Length)`.
      - The direct call to `_audioEndPoint.GotAudioRtp()` for received packets has been removed.
  - **Logging:** Implemented 1% probabilistic logging for high-frequency send/receive events.

- **`AudioService.cs`:**

  - **Renaming:** General cleanup of "MP3" to "Audio File" (e.g., `UseAudioFileInput`).
  - **Packetization & Encoding (Sending Path):**
    - Introduced constants for PCMU audio (8kHz, 1ch, 20ms packets = 160 bytes).
    - `EncodedAudioPacketAvailable` event now carries 20ms, 160-byte PCMU packets.
    - **Microphone Input:** `OnWaveInDataAvailable` now captures at 48kHz, buffers, resamples to 8kHz PCM, encodes to PCMU, and emits 160-byte packets.
    - **Audio File Input (`AudioFilePlaybackCallback`):**
      - If input WAV is already 8kHz/Mono/MuLaw (like `test.wav`), it reads and sends 160-byte PCMU packets.
      - Logs a TODO for other WAV formats (conversion pipeline pending).
  - **Playback of Received Audio:**
    - `CreatePeerPlayback`: `BufferedWaveProvider` now initialized with 8kHz, 16-bit PCM `WaveFormat` (`_pcm8KhzFormat`).
    - `PlayAudio`: Now decodes incoming PCMU byte arrays (from `WebRtcService`) into 8kHz, 16-bit PCM before adding to the buffer.
    - Added helper `MuLawWaveStream` for decoding.
  - **Logging:** Implemented 1% probabilistic logging for audio processing events.

- **Build & Build Fixes:**

  - Iteratively resolved multiple build errors arising from the refactoring, including type mismatches for event arguments and incorrect method calls.
  - Successfully resolved the persistent `OnTrack` build error by removing the handler and adopting the `AudioRemoteTrack.Ssrc` approach.

- **`test.wav` File Verification:**
  - Used `ffprobe` to confirm `test.wav` (located at the workspace root) is:
    - Codec: PCM mu-Law (G.711 mu-Law)
    - Sample Rate: 8000 Hz
    - Channels: 1 (Mono)
  - This format is suitable for direct use by `AudioService` without on-the-fly conversion.

## Current State & Hypothesis

- The project **builds successfully**.
- The audio sending pipeline _should_ be correctly capturing, encoding (to PCMU 8kHz, 20ms/160-byte packets), and transmitting audio via `PeerConnection.SendAudio`.
- The audio receiving pipeline in `WebRtcService` _should_ now identify the peer based on SSRC (if successfully captured via `AudioRemoteTrack.Ssrc` or if it matches an SSRC from a previously received packet) and pass the raw PCMU payload to `AudioService.PlayAudio`.
- `AudioService.PlayAudio` _should_ then decode the PCMU data to PCM and buffer it for playback.
- It is hypothesized that with these changes, audio sent from one client should be received, processed, and played back on the other client. The key is whether the SSRC association works reliably.

## Next Steps for Testing

1.  **Core Audio Path Test (End-to-End):**

    - Run two clients on the same machine.
    - **Client A (Sender):** Use microphone input.
    - **Client B (Receiver):**
    - **Expected Outcome:** Audio from Client A's microphone is heard on Client B. UI indicators for "audio playing" appear for the peer on Client B.

2.  **Log Monitoring (CRITICAL for validation):**

    - **During Connection Setup (Both Clients):**
      - `WebRtcService`: Look for successful SSRC association: `Associated remote audio SSRC {ssrc} with offering peer {peerId}` or `Associated remote audio SSRC {ssrc} with answering peer {peerId}`.
      - If not seen, or if `[WARNING] Could not get remote audio SSRC...` appears, this is a primary point of failure to investigate for SSRC capture.
    - **Client A (Sending Client):**
      - `AudioService`: `(Sampled Log) Mic: Sent {bytesRead} PCMU bytes...`
      - `WebRtcService`: `(Sampled Log) OnEncodedAudioPacketReadyToSend: samples={samplesInPacket}, hasAudio={true}...`
      - `WebRtcService`: `(Sampled Log) Sent audio packet via PeerConnection.SendAudio to {peerId}: duration={samplesInPacket}, size={audioData.Length}...` (ensure `hasAudio` was true prior).
    - **Client B (Receiving Client):**
      - `WebRtcService`: `(Sampled Log) RTP for SSRC {ssrc} (Peer {foundPeerId}): ... hasAudio={true}`. **Crucially, `foundPeerId` must not be null.** If it's null, but an SSRC is seen, the SSRC association failed.
      - `AudioService`: `Played {bytesDecoded} decoded PCM bytes from peer {peerId} ...`. This confirms audio reached playback.

3.  **`test.wav` File Input Test:**
    - Modify one client to use `UseAudioFileInput = true`. The `AUDIO_FILE_PATH` in `AudioService.cs` points to "test.wav" which is in the workspace root. Ensure the application can access `..\test.wav` from its execution directory (`proxchat-client\bin\Debug...`). _This might require adjusting `AUDIO_FILE_PATH` to be an absolute path or copying `test.wav` into the output directory for reliable testing._
    - **Client A (Sender):** Use `test.wav` input.
    - **Client B (Receiver):**
    - **Expected Outcome:** Audio from `test.wav` is heard on Client B.
    - **Log Monitoring (Client A):**
      - `AudioService`: `(Sampled Log) File: Sent 160 bytes of audio data from file source (format: MuLaw).`
      - Relevant `WebRtcService` sending logs.
    - **Log Monitoring (Client B):** Same as microphone test.

## Things to Look Out For / Potential Issues

- **SSRC Association Failure:**
  - Logs: `[WARNING] Could not get remote audio SSRC...` (during offer/answer).
  - Logs: `[WARNING] Received audio RTP packet with SSRC {ssrc} but no associated peer found.` (in `OnRtpPacketReceived`).
  - **If this occurs:** The `AudioRemoteTrack.Ssrc` method isn't reliable enough. We may need a fallback to dynamically associate an unknown SSRC from the _first_ RTP packet received from a given remote RTP endpoint, perhaps correlating with a peer connection that just completed ICE and is in `connected` state but doesn't yet have an SSRC.
- **No Audio Heard Despite Logs:**
  - If `AudioService.PlayAudio` logs appear but no audio is heard: Check system volume, output device selection in Windows, and potential issues within NAudio's `WaveOutEvent` or `BufferedWaveProvider` (e.g., buffer underruns, incorrect device).
  - UI "audio playing" indicator not working: Check data binding or event handling in the UI related to this.
- **Silent Packets:** If `hasAudio` is consistently false in logs, but audio _should_ be sent (e.g., microphone is active), review the silence detection logic in `AudioService` and `WebRtcService` or the audio capture itself.
- **File Path for `test.wav`:** If `AudioFilePlaybackCallback` logs errors like "file not found" or if it falls into the "PCM/Other file format to PCMU conversion not fully implemented" path, the file path needs to be corrected or the file needs to be accessible. The `AUDIO_FILE_PATH` is currently `"test.wav"`. If running from `proxchat-client/bin/...`, this needs to be `../../test.wav` or an absolute path.
- **Decoding Errors:** `Error decoding/playing audio for peer...` in `AudioService.PlayAudio`.
- **Application Cleanup Errors:** Errors on closing the application (known, lower priority).

## Lower Priority Next Steps (After Core Audio Flow Works)

- **Address TODO for PCM Audio File Input:** Implement full resampling/encoding for generic PCM WAV files in `AudioService.AudioFilePlaybackCallback`.
- **General Audio Quality:** Test for delays, jitter, dropouts.
- **Robustness:** Test with network variations (if possible), multiple peers.
- **UI/UX Refinements:** Ensure all indicators and controls are intuitive.
