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
    - **SSRC Association:**
      - Implemented logic in `OnOfferReceived` and `OnAnswerReceived`:
        - First, it attempts to capture the remote audio SSRC from `pc.AudioRemoteTrack.Ssrc` after `setRemoteDescription` is successfully called.
        - **Fallback SDP Parsing:** If `AudioRemoteTrack.Ssrc` is `0` (unavailable), it now attempts to parse the `a=ssrc:` line directly from the raw SDP string of the offer/answer to obtain the remote SSRC. This SSRC is stored in `PeerConnectionState.RemoteAudioSsrc`.
    - Modified `OnRtpPacketReceived`:
      - It now uses the `rtpPacket.Header.SyncSource` (incoming SSRC) to find the corresponding `peerId`.
      - **Direct SSRC Match:** It first tries to match `incomingSsrc` with a known `RemoteAudioSsrc` in `_peerConnections`.
      - **Refined Dynamic SSRC Association:** If no direct match is found and `incomingSsrc` is valid, it attempts to find a connected peer in `_peerConnections` that has `RemoteAudioSsrc == 0` (i.e., its SSRC is not yet known). If such a peer is found, `incomingSsrc` (from the current RTP packet) is assigned to that peer's `RemoteAudioSsrc`.
      - If a `peerId` is successfully found (either by direct match or dynamic association), it calls `_audioService.PlayAudio(foundPeerId, rtpPacket.Payload, rtpPacket.Payload.Length)`.
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
    - `PlayAudio`:
      - Now decodes incoming PCMU byte arrays (from `WebRtcService`) into 8kHz, 16-bit PCM before adding to the buffer.
      - **Proactive PeerPlayback Creation:** If `PlayAudio` is called for a `peerId` that doesn't have an existing `PeerPlayback` object, it now attempts to create one on-the-fly by calling `CreatePeerPlayback(peerId)`. This makes the audio pipeline more resilient to audio packets arriving before explicit peer setup (e.g., via position updates).
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
- The audio receiving pipeline in `WebRtcService` _should_ now:
  - Reliably identify the remote peer's SSRC either via `AudioRemoteTrack.Ssrc` or by parsing the SDP.
  - If initial SSRC association fails, the refined dynamic SSRC logic in `OnRtpPacketReceived` should associate an incoming SSRC with a peer that has a connected state but no SSRC yet.
  - Pass the raw PCMU payload to `AudioService.PlayAudio` for the correctly identified peer.
- `AudioService.PlayAudio` _should_ now:
  - Create playback components for a peer if they don't already exist.
  - Decode the PCMU data to PCM and buffer it for playback.
- It is hypothesized that with these SSRC handling improvements and proactive `PeerPlayback` creation, audio sent from one client should be reliably received, processed, and played back on the other client. The primary SSRC association should be more robust.

## Next Steps for Testing

1.  **Core Audio Path Test (End-to-End):**

    - Run two clients on the same machine.
    - **Client A (Sender):** Use microphone input.
    - **Client B (Receiver):**
    - **Expected Outcome:** Audio from Client A's microphone is heard on Client B. UI indicators for "audio playing" appear for the peer on Client B.

2.  **Log Monitoring (CRITICAL for validation):**

    - **During Connection Setup (Both Clients):**
      - `WebRtcService`:
        - Look for successful SSRC association: `Associated remote audio SSRC {ssrc} (from AudioRemoteTrack) ...` OR `Associated remote audio SSRC {ssrc} (from SDP parse of ...) ...`.
        - If both fail, and logs show `[ERROR] Failed to parse SSRC from ... SDP...`, this is a point of failure.
        - Monitor `Dynamically associating incoming SSRC {incomingSsrc} with peer {entry.Key}...` if the above fails.
    - **Client A (Sending Client):**
      - `AudioService`: `(Sampled Log) Mic: Sent {bytesRead} PCMU bytes...`
      - `WebRtcService`: `(Sampled Log) OnEncodedAudioPacketReadyToSend: samples={samplesInPacket}, hasAudio={true}...`
      - `WebRtcService`: `(Sampled Log) Sent audio packet via PeerConnection.SendAudio to {peerId}: duration={samplesInPacket}, size={audioData.Length}...` (ensure `hasAudio` was true prior).
    - **Client B (Receiving Client):**
      - `WebRtcService`: `(Sampled Log) RTP for SSRC {ssrc} (Peer {foundPeerId}): ... hasAudio={true}`.
        - **Crucially, `foundPeerId` must not be null.** If it's null, but an SSRC is seen, the SSRC association (initial and dynamic) failed.
      - `AudioService`: `PlayAudio: PeerPlayback for peer {peerId} not found. Attempting to create.` (if audio arrives before typical setup).
      - `AudioService`: `PlayAudio: Successfully created PeerPlayback for {peerId} on-the-fly.` (if applicable).
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
  - Logs: `[ERROR] Failed to parse SSRC from ... SDP...` (during offer/answer).
  - Logs: `[WARNING] Received audio RTP packet with SSRC {ssrc} but no associated peer found.` (in `OnRtpPacketReceived`). This would indicate both SDP parsing and dynamic association failed.
  - **If this occurs:**
    - Verify the SDP content in the logs to ensure `a=ssrc:` lines are present for audio.
    - Re-examine the `ParseSsrcFromSdp` regex or logic.
    - The dynamic SSRC association might still need refinement if multiple peers connect very rapidly and SDP parsing fails for all of them.
- **Audio Playback Issues Despite Peer Found:**
  - `AudioService` logs show `Played ... bytes...` for the correct peer, but no audio is heard: Check system volume, output device selection in Windows, potential NAudio issues.
  - `AudioService` logs `[ERROR] PlayAudio: Failed to create PeerPlayback for {peerId}...` or `[WARNING] PlayAudio: Peer {peerId} has null playback components (WaveOut or Buffer is null even after potential creation)...`. This indicates an issue in `CreatePeerPlayback`.
- **Silent Packets:** If `hasAudio` is consistently false in logs, but audio _should_ be sent, review silence detection or capture.
- **File Path for `test.wav`:** If `
