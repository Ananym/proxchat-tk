# Proximity Chat TK

Mesh webrtc proximity voice chat app for a desktop videogame.
Consists of a dotnet client application and a rust containerized signalling server.

**Signaling Flow:**
1.  Client connects via WebSocket to the signalling server.
2.  Client generates a unique ID (GUID) upon startup.
3.  Client periodically sends `UpdatePosition` messages (containing its client ID, map ID, X/Y coords) to the server.
    *   Updates are sent every 1 second if position/map changed significantly, or every 10 seconds otherwise.
4.  Server uses the client-provided ID to track connections and positions.
5.  Server calculates proximity based on received positions.
6.  Server responds to `UpdatePosition` with a `NearbyPeers` message, containing a list of client IDs currently nearby on the same map.
7.  Clients compare the `NearbyPeers` list with their current connections:
    *   **New Peers:** Initiate WebRTC connection setup.
        *   Initiator role is determined by lexicographical comparison of client IDs to prevent glare.
        *   WebRTC Offer/Answer/ICE Candidates are exchanged via the signaling server using `SendOffer`/`ReceiveOffer`, `SendAnswer`/`ReceiveAnswer`, `SendIceCandidate`/`ReceiveIceCandidate` messages.
    *   **Peers No Longer Nearby:** Disconnect the corresponding WebRTC connection (client-side).
8.  Server removes client state if no `UpdatePosition` is received for 15 seconds (timeout).

**WebRTC Connection:**
*   Direct Peer-to-Peer (P2P) connection established via WebRTC.
*   Uses Google's public STUN servers for NAT traversal; no TURN server.
*   Audio stream is transmitted over the connection.
*   A data channel is established to send `map_id`, `x`, and `y` coordinates directly to the connected peer.
*   Client uses received coordinates from the data channel to calculate distance to the peer and adjust the playback volume of their audio stream.

**Client Implementation Details:**
*   Acquires map ID and X/Y coords from the game (`NexusTK.exe`) via a memory-mapped file.
*   Windows-only, using a modern dotnet framework (e.g., .NET 6/7/8 with WPF or WinUI).
*   Built as a single-file, self-contained executable.
*   Loads server host/port configuration from a file (`config.json`).
*   Simple UI:
    *   Start/Stop button.
    *   Status indicator.
    *   List of connected peer IDs with individual mute toggles and distance display.
    *   Dropdown for selecting audio input device.
    *   Indicator for current microphone audio level.
    *   Slider for scaling overall output volume.
    *   Optional Push-to-Talk (PTT):
        *   Checkbox to enable/disable.
        *   Button to edit the PTT key.
        *   Uses the same single audio capture stream for all connections.

**Server Implementation Details:**
*   Rust-based application.
*   Designed to be containerized (Dockerfile provided).
*   Manages WebSocket connections and client state (position, connection mapping, last update time).
*   Relays signaling messages between appropriate clients.
*   Uses high-level libraries where possible (e.g., `tokio`, `tokio-tungstenite`, `serde`).
