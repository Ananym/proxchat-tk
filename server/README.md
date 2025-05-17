# Proximity Chat Signaling Server

A WebSocket-based signaling server for facilitating proximity-based WebRTC mesh connections.

## Protocol Documentation

### WebSocket Connection

Connect to the server at `ws://localhost:8080` (or the configured host/port in production).

### Message Format

All messages are JSON with a `type` field indicating the message type and a `data` field containing the payload.

#### Client -> Server Messages

1. Update Position
```json
{
    "type": "UpdatePosition",
    "data": {
        "client_id": "string",
        "map_id": "integer",
        "x": "integer",
        "y": "integer"
    }
}
```

2. Send Offer
```json
{
    "type": "SendOffer",
    "data": {
        "target_id": "string",
        "offer": "string"
    }
}
```

3. Send Answer
```json
{
    "type": "SendAnswer",
    "data": {
        "target_id": "string",
        "answer": "string"
    }
}
```

4. Send Ice Candidate
```json
{
    "type": "SendIceCandidate",
    "data": {
        "target_id": "string",
        "candidate": "string"
    }
}
```

5. Disconnect
```json
{
    "type": "Disconnect"
}
```

#### Server -> Client Messages

1. Nearby Peers
```json
{
    "type": "NearbyPeers",
    "data": ["client_id1", "client_id2", ...]
}
```

2. Receive Offer
```json
{
    "type": "ReceiveOffer",
    "data": {
        "sender_id": "string",
        "offer": "string"
    }
}
```

3. Receive Answer
```json
{
    "type": "ReceiveAnswer",
    "data": {
        "sender_id": "string",
        "answer": "string"
    }
}
```

4. Receive Ice Candidate
```json
{
    "type": "ReceiveIceCandidate",
    "data": {
        "sender_id": "string",
        "candidate": "string"
    }
}
```

5. Error
```json
{
    "type": "Error",
    "data": "error message string"
}
```

### WebRTC Connection Process (Overview)

1. Client A sends position update to server.
2. Server responds with nearby client IDs.
3. For each new nearby client ID not already connected:
   a. Create new RTCPeerConnection with STUN configuration.
   b. Create data channel for position updates.
   c. Create audio transceiver for voice.
   d. Create offer and set local description.
   e. Exchange offer/answer and ICE candidates through the WebSocket Signaling Server.
   f. Once connected, stream audio and send position updates through data channel.

4. For each connected peer no longer in the nearby list:
   a. Close RTCPeerConnection.
   b. Clean up resources.

### WebRTC Configuration

```javascript
const rtcConfig = {
    iceServers: [
        {
            urls: "stun:stun.l.google.com:19302"
        }
    ]
};
```

### Position Updates Over Data Channel

Send position updates directly to connected peers using the following JSON format over the established WebRTC data channel:
```json
{
    "map_id": "integer",
    "x": "integer",
    "y": "integer",
    "character_name": "string"
}
```

### Audio Stream Management

1. Capture single audio input stream.
2. For each peer connection:
   - Add audio track to connection.
   - Receive remote audio track.
   - Volume can be adjusted based on distance calculated from received peer positions.

### WebRTC Connection Process (Detailed)

1. **Discovery**:
   a. Client A connects to the Signaling Server via WebSocket.
   b. Client A sends `UpdatePosition` messages periodically.
   c. Client B connects and sends `UpdatePosition`.
   d. The Server calculates proximity. If A and B are close and on the same map, the server sends a `NearbyPeers` message to A (including B's ID) and to B (including A's ID).

2. **Connection Initiation**:
   a. Upon receiving `NearbyPeers`, Client A sees Client B's ID and decides to connect (if not already connected).
   b. Client A creates an `RTCPeerConnection` for Client B.
   c. Client A generates an SDP Offer.
   d. Client A sends a `SendOffer` message to the Server, containing the Offer and specifying Client B's ID as the `target_id`.
   e. The Signaling Server relays the offer by sending a `ReceiveOffer` message to Client B.

3. **Connection Acceptance**:
   a. Client B receives the `ReceiveOffer` message.
   b. Client B creates its `RTCPeerConnection` for Client A and sets the received offer as the remote description.
   c. Client B generates an SDP Answer.
   d. Client B sends a `SendAnswer` message to the Server, containing the Answer and Client A's ID as the `target_id`.
   e. The Signaling Server relays the answer by sending a `ReceiveAnswer` message to Client A.

4. **Finalizing Connection**:
   a. Client A receives the `ReceiveAnswer` message and sets the Answer as the remote description.
   b. Both clients start exchanging ICE candidates to find the best network path.
   c. When a client generates an ICE candidate, it sends a `SendIceCandidate` message to the Server.
   d. The Server relays this by sending a `ReceiveIceCandidate` message to the target peer.
   e. The receiving client adds the candidate to its peer connection.
   f. Once ICE negotiation is complete, the direct P2P WebRTC connection is established.

5. **Disconnection**:
   a. If clients move too far apart or change maps, the server will stop including them in each other's `NearbyPeers` lists.
   b. The client notices a peer is no longer in the `NearbyPeers` list and closes the corresponding `RTCPeerConnection`.
   c. If a client disconnects from the WebSocket, the server cleans up its state.

## Building and Running

### Local Development
```powershell
cargo run
```

### Docker
```powershell
docker build -t prox-chat-server .
docker run -p 8080:8080 prox-chat-server
``` 