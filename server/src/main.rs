use futures_util::{SinkExt, StreamExt};
use log::{error, info, warn};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::net::SocketAddr;
use std::sync::Arc;
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::{mpsc, RwLock};
use tokio::time::{self, Duration, Instant};
use tokio_tungstenite::tungstenite::Message;
use uuid::Uuid;
use serde_json;

// Client position data structure
#[derive(Debug, Clone, Serialize, Deserialize)]
struct ClientPosition {
    client_id: String,
    map_id: i32,
    x: i32,
    y: i32,
    channel: i32,
}

// Message types for client -> server communication
#[derive(Debug, Serialize, Deserialize)]
#[serde(tag = "type", content = "data")]
enum ClientMessage {
    UpdatePosition(ClientPosition),
    RequestPeerRefresh, // request fresh nearby peer list (e.g., after failed connection)
    // Add messages for sending signaling data
    SendOffer { target_id: String, offer: String }, // Assuming offer is JSON string
    SendAnswer { target_id: String, answer: String }, // Assuming answer is JSON string
    SendIceCandidate { target_id: String, candidate: String }, // Assuming candidate is JSON string
    Disconnect,
}

// Add message types for server -> client communication
#[derive(Debug, Serialize, Deserialize, Clone)]
#[serde(tag = "type", content = "data")]
enum ServerMessage {
    NearbyPeers(Vec<String>),
    ReceiveOffer { sender_id: String, offer: String },
    ReceiveAnswer { sender_id: String, answer: String },
    ReceiveIceCandidate { sender_id: String, candidate: String },
    Error(String), // Optional: To send error messages back to client
}

// Shared state between all connections
struct ServerState {
    // Separate position data from connection channels
    positions: HashMap<String, ClientPosition>,
    // cache last sent nearby lists to avoid redundant updates
    last_nearby_lists: HashMap<String, HashSet<String>>,
    // Map connection_id (server-generated UUID) to a channel sender for sending messages *to* that client
    connections: HashMap<String, mpsc::Sender<ServerMessage>>,
    // Map client_id (client-provided GUID) to connection_id (server-generated UUID)
    // This is needed to route messages targeted by client_id
    client_id_to_connection_id: HashMap<String, String>,
    // Track the last time a client sent an UpdatePosition
    last_update_time: HashMap<String, Instant>,
}

impl ServerState {
    fn new() -> Self {
        ServerState {
            positions: HashMap::new(),
            last_nearby_lists: HashMap::new(),
            connections: HashMap::new(),
            client_id_to_connection_id: HashMap::new(),
            last_update_time: HashMap::new(),
        }
    }

    // highly optimized proximity calculation for small client counts
    fn get_nearby_clients(&self, pos: &ClientPosition, range: f32) -> Vec<String> {
        const RANGE_SQUARED: f32 = 20.0 * 20.0; // pre-calculate to avoid sqrt
        
        self.positions
            .iter()
            .filter_map(|(id, other_pos)| {
                // early exit conditions (cheap comparisons first)
                if id == &pos.client_id { return None; }
                if other_pos.map_id != pos.map_id { return None; }
                if other_pos.channel != pos.channel { return None; }
                
                // squared distance check (no sqrt needed)
                let dx = other_pos.x - pos.x;
                let dy = other_pos.y - pos.y;
                let distance_squared = (dx * dx + dy * dy) as f32;
                
                if distance_squared <= RANGE_SQUARED {
                    Some(id.clone())
                } else {
                    None
                }
            })
            .collect()
    }

    // efficient update that only notifies for NEW peer introductions
    fn update_position_and_notify(&mut self, new_pos: ClientPosition, sender_tx: &mpsc::Sender<ServerMessage>) -> Vec<(String, mpsc::Sender<ServerMessage>)> {
        let client_id = new_pos.client_id.clone();
        let mut notifications = Vec::new();
        
        // update position and timestamp
        self.positions.insert(client_id.clone(), new_pos.clone());
        self.last_update_time.insert(client_id.clone(), Instant::now());
        
        // get new nearby list for this client
        let nearby_for_sender = self.get_nearby_clients(&new_pos, 20.0);
        let nearby_set: HashSet<String> = nearby_for_sender.iter().cloned().collect();
        
        // only notify for NEW peers (not for position changes of existing peers)
        let previous_nearby = self.last_nearby_lists.get(&client_id).cloned().unwrap_or_default();
        let new_peers: HashSet<String> = nearby_set.difference(&previous_nearby).cloned().collect();
        let lost_peers: HashSet<String> = previous_nearby.difference(&nearby_set).cloned().collect();
        
        // only send update if there are actually new or lost peers
        if !new_peers.is_empty() || !lost_peers.is_empty() {
            self.last_nearby_lists.insert(client_id.clone(), nearby_set.clone());
            notifications.push((client_id.clone(), sender_tx.clone()));
            
            info!("Client {} peer changes: +{} new peers, -{} lost peers", 
                  client_id, new_peers.len(), lost_peers.len());
        }
        
        // notify existing peers that this client is now nearby (introduction in reverse)
        for new_peer_id in new_peers {
            if let Some(peer_pos) = self.positions.get(&new_peer_id) {
                // check if this moving client is NEW to the peer's nearby list
                let peer_nearby = self.get_nearby_clients(peer_pos, 20.0);
                let peer_nearby_set: HashSet<String> = peer_nearby.iter().cloned().collect();
                let peer_previous_nearby = self.last_nearby_lists.get(&new_peer_id).cloned().unwrap_or_default();
                
                // if the moving client is new to this peer's view, notify the peer
                if !peer_previous_nearby.contains(&client_id) && peer_nearby_set.contains(&client_id) {
                    self.last_nearby_lists.insert(new_peer_id.clone(), peer_nearby_set);
                    
                    if let Some(peer_conn_id) = self.client_id_to_connection_id.get(&new_peer_id) {
                        if let Some(peer_tx) = self.connections.get(peer_conn_id) {
                            notifications.push((new_peer_id.clone(), peer_tx.clone()));
                            info!("Notifying peer {} of new client {}", new_peer_id, client_id);
                        }
                    }
                }
            }
        }
        
        notifications
    }
}

async fn handle_connection(
    state: Arc<RwLock<ServerState>>,
    raw_stream: TcpStream,
    addr: SocketAddr,
) {
    info!("New connection attempt from: {}", addr);

    // try websocket upgrade directly - if it's a health check, it will fail gracefully
    let ws_stream = match tokio_tungstenite::accept_async(raw_stream).await {
        Ok(stream) => stream,
        Err(e) => {
            // could be a health check or other HTTP request
            info!("WebSocket handshake failed from {}: {} (likely health check)", addr, e);
            return;
        }
    };

    info!("WebSocket connection established from: {}", addr);

    let (mut ws_sender, mut ws_receiver) = ws_stream.split();
    // Server-generated ID to uniquely identify this WebSocket connection instance
    let connection_id = Uuid::new_v4().to_string();

    // Create a channel for sending messages to this client's WebSocket task
    let (tx, mut rx) = mpsc::channel::<ServerMessage>(100); // Buffer size 100

    // Store the sender tx in the shared state using the connection_id
    {
        let mut state_write = state.write().await;
        state_write.connections.insert(connection_id.clone(), tx.clone());
        info!("Connection established: {} ({})", connection_id, addr);
    }

    // Task: Sends messages from the channel `rx` to the client's WebSocket `ws_sender`
    let send_task_connection_id = connection_id.clone(); // Clone for the send task
    let send_task = tokio::spawn(async move {
        while let Some(msg) = rx.recv().await {
            match serde_json::to_string(&msg) {
                Ok(msg_str) => {
                    if ws_sender.send(Message::Text(msg_str)).await.is_err() {
                        // Error sending, client likely disconnected
                        // Log with connection_id as client_id might not be known/relevant here
                        error!("Failed to send message to {}: WebSocket send error.", send_task_connection_id);
                        break;
                    }
                }
                Err(e) => {
                    error!("Failed to serialize ServerMessage for {}: {}", send_task_connection_id, e);
                }
            }
        }
        // When rx closes or send fails, this task ends.
    });

    // Task: Receives messages from the client's WebSocket `ws_receiver` and handles them
    let recv_task_state = Arc::clone(&state);
    let recv_task_connection_id = connection_id.clone(); // Clone for the receive task
    let recv_task_tx = tx.clone(); // Clone tx for sending messages back to this client

    let recv_task = tokio::spawn(async move {
        let state = recv_task_state;
        let connection_id = recv_task_connection_id;
        let tx = recv_task_tx;
        // Store the client-provided ID once received
        let mut registered_client_id: Option<String> = None;

        while let Some(msg_result) = ws_receiver.next().await {
            let msg = match msg_result {
                Ok(msg) => msg,
                Err(e) => {
                    error!("WebSocket error receiving from {} ({}): {}",
                           registered_client_id.as_deref().unwrap_or(&connection_id), addr, e);
                    break; // Exit loop on WebSocket error
                }
            };

            if msg.is_close() {
                info!("Received close frame from {} ({})",
                       registered_client_id.as_deref().unwrap_or(&connection_id), addr);
                break; // Exit loop if client sent close frame
            }

            if let Message::Text(text) = msg {
                let client_msg: ClientMessage = match serde_json::from_str(&text) {
                    Ok(msg) => msg,
                    Err(e) => {
                        error!("Failed to parse message from {} ({}): {}. Message: '{}'",
                               registered_client_id.as_deref().unwrap_or(&connection_id), addr, e, text);
                        let _ = tx.send(ServerMessage::Error(format!("Invalid message format: {}", e))).await;
                        continue; // Skip processing this message
                    }
                };

                // Ensure client has registered with UpdatePosition before processing other messages
                if registered_client_id.is_none() && !matches!(client_msg, ClientMessage::UpdatePosition(_)) {
                    error!("Received non-UpdatePosition message from unregistered connection {} ({}): {:?}",
                           connection_id, addr, client_msg);
                    let _ = tx.send(ServerMessage::Error("Client must send UpdatePosition first.".to_string())).await;
                    continue;
                }

                match client_msg {
                    ClientMessage::UpdatePosition(pos) => {
                        let client_id_from_payload = pos.client_id.clone();

                        let mut state_write = state.write().await;

                        // Handle first UpdatePosition: Register client_id
                        if registered_client_id.is_none() {
                            // Check if this client_id is already mapped to another connection
                            if let Some(existing_conn_id) = state_write.client_id_to_connection_id.get(&client_id_from_payload) {
                                // Simple approach: Log warning, assume client reconnected, update mapping.
                                warn!("Client ID {} already registered to connection {}. Re-registering to {}",
                                       client_id_from_payload, existing_conn_id, connection_id);
                                // Remove old connection's entry if it exists (might be slightly inconsistent if old conn is still cleaning up)
                                if let Some(_old_tx) = state_write.connections.get(existing_conn_id) {
                                    // Maybe send a disconnect message to the old connection?
                                    // _old_tx.send(ServerMessage::Error("Disconnected: Replaced by new connection".to_string())).await;
                                }
                                // clean up ALL old client data to prevent stale position data issues
                                state_write.client_id_to_connection_id.remove(&client_id_from_payload);
                                state_write.positions.remove(&client_id_from_payload);
                                state_write.last_update_time.remove(&client_id_from_payload);
                                state_write.last_nearby_lists.remove(&client_id_from_payload);
                                // note: not removing from connections as old connection will clean itself up
                            }

                            state_write.client_id_to_connection_id.insert(client_id_from_payload.clone(), connection_id.clone());
                            registered_client_id = Some(client_id_from_payload.clone());
                            info!("Client registered: ID {} mapped to connection {} ({})",
                                  client_id_from_payload, connection_id, addr);
                        }
                        // If already registered, ensure the client_id hasn't changed (or handle as error)
                        else if registered_client_id.as_ref() != Some(&client_id_from_payload) {
                            error!("Client {} (connection {}) sent UpdatePosition with conflicting ID {}. Ignoring.",
                                   registered_client_id.as_ref().unwrap(), connection_id, client_id_from_payload);
                            drop(state_write); // Release lock before continuing
                            continue;
                        }

                        // Use optimized update that only sends notifications when nearby lists change
                        let notifications = state_write.update_position_and_notify(pos, &tx);
                        
                        // Release write lock before sending notifications to reduce contention
                        drop(state_write);

                        // Send notifications outside of write lock
                        for (notify_client_id, notify_tx) in notifications {
                            // get fresh nearby list for this client
                            let state_read = state.read().await;
                            if let Some(client_pos) = state_read.positions.get(&notify_client_id) {
                                let nearby_list = state_read.get_nearby_clients(client_pos, 20.0);
                                drop(state_read);
                                
                                let response = ServerMessage::NearbyPeers(nearby_list);
                                if let Err(e) = notify_tx.send(response).await {
                                    warn!("Failed to send NearbyPeers update to {}: {}", notify_client_id, e);
                                }
                            }
                        }
                    }
                    ClientMessage::RequestPeerRefresh => {
                        if let Some(sender_id) = registered_client_id.as_ref() {
                            // send current nearby peers regardless of cache
                            let state_read = state.read().await;
                            if let Some(client_pos) = state_read.positions.get(sender_id) {
                                let nearby_list = state_read.get_nearby_clients(client_pos, 20.0);
                                drop(state_read);
                                
                                let response = ServerMessage::NearbyPeers(nearby_list);
                                if let Err(e) = tx.send(response).await {
                                    warn!("Failed to send peer refresh to {}: {}", sender_id, e);
                                } else {
                                    info!("Sent peer refresh to {} (explicit request)", sender_id);
                                }
                            }
                        } else {
                            error!("RequestPeerRefresh received before client ID registration (connection {}).", connection_id);
                        }
                    }
                    ClientMessage::SendOffer { target_id, offer } => {
                        if let Some(sender_id) = registered_client_id.as_ref() {
                            let state_read = state.read().await;
                            if let Some(target_connection_id) = state_read.client_id_to_connection_id.get(&target_id) {
                                if let Some(target_tx) = state_read.connections.get(target_connection_id) {
                                    let offer_msg = ServerMessage::ReceiveOffer { sender_id: sender_id.clone(), offer };
                                    if let Err(e) = target_tx.send(offer_msg).await {
                                        error!("Failed to relay offer from {} to {}: {}", sender_id, target_id, e);
                                        let _ = tx.send(ServerMessage::Error(format!("Failed to send offer to {}", target_id))).await;
                                    }
                                } else {
                                    // client_id_to_connection_id mapping exists, but connection doesn't? Should not happen.
                                    error!("Internal state inconsistency: Connection {} not found for client {}", target_connection_id, target_id);
                                    let _ = tx.send(ServerMessage::Error(format!("Internal error relaying offer to {}", target_id))).await;
                                }
                            } else {
                                error!("Target client {} not found for offer from {}", target_id, sender_id);
                                let _ = tx.send(ServerMessage::Error(format!("Client {} not found", target_id))).await;
                            }
                        } else {
                            error!("SendOffer received before client ID registration (connection {}).", connection_id);
                        }
                    }
                    ClientMessage::SendAnswer { target_id, answer } => {
                        if let Some(sender_id) = registered_client_id.as_ref() {
                             let state_read = state.read().await;
                            if let Some(target_connection_id) = state_read.client_id_to_connection_id.get(&target_id) {
                                if let Some(target_tx) = state_read.connections.get(target_connection_id) {
                                    let answer_msg = ServerMessage::ReceiveAnswer { sender_id: sender_id.clone(), answer };
                                    if let Err(e) = target_tx.send(answer_msg).await {
                                        error!("Failed to relay answer from {} to {}: {}", sender_id, target_id, e);
                                        let _ = tx.send(ServerMessage::Error(format!("Failed to send answer to {}", target_id))).await;
                                    }
                                } else {
                                    error!("Internal state inconsistency: Connection {} not found for client {}", target_connection_id, target_id);
                                    let _ = tx.send(ServerMessage::Error(format!("Internal error relaying answer to {}", target_id))).await;
                                }
                            } else {
                                error!("Target client {} not found for answer from {}", target_id, sender_id);
                                let _ = tx.send(ServerMessage::Error(format!("Client {} not found", target_id))).await;
                            }
                         } else {
                            error!("SendAnswer received before client ID registration (connection {}).", connection_id);
                        }
                    }
                    ClientMessage::SendIceCandidate { target_id, candidate } => {
                        if let Some(sender_id) = registered_client_id.as_ref() {
                            let state_read = state.read().await;
                            if let Some(target_connection_id) = state_read.client_id_to_connection_id.get(&target_id) {
                                if let Some(target_tx) = state_read.connections.get(target_connection_id) {
                                    let candidate_msg = ServerMessage::ReceiveIceCandidate { sender_id: sender_id.clone(), candidate };
                                    if let Err(e) = target_tx.send(candidate_msg).await {
                                        // Don't log error for every ICE candidate failure, might be too noisy
                                        // Don't notify sender either, usually transient
                                    }
                                } // else: Internal inconsistency or target disconnected, ignore ICE
                            } // else: Target client not found, ignore ICE
                         } else {
                            // Ignore ICE if client not registered yet
                            // error!("SendIceCandidate received before client ID registration (connection {}).", connection_id);
                        }
                    }
                    ClientMessage::Disconnect => {
                        info!("Received Disconnect message from {} ({})",
                               registered_client_id.as_deref().unwrap_or(&connection_id), addr);
                        break; // Exit the loop
                    }
                }
            } else if msg.is_binary() {
                 warn!("Received unexpected binary message from {} ({})",
                       registered_client_id.as_deref().unwrap_or(&connection_id), addr);
            } // Ignore Ping/Pong etc for now
        }

        // Return the connection_id and the registered client_id (if any) when the loop exits
        (connection_id, registered_client_id)
    });

    // Wait for the receiving task to finish.
    let (disconnected_connection_id, disconnected_client_id_option) = match recv_task.await {
        Ok(ids) => ids,
        Err(e) => {
             // Use the original connection_id for cleanup in case of panic
             error!("Receive task panicked for connection {}: {}", connection_id, e);
             (connection_id, None) // Assume client_id was not registered if task panicked
        }
    };

    // Abort the sending task as it's no longer needed and to close the channel
    send_task.abort();

    // Clean up state
    {
        let mut state_write = state.write().await;
        // Remove the connection channel using connection_id
        state_write.connections.remove(&disconnected_connection_id);

        // If a client_id was registered for this connection, remove its mappings
        if let Some(disconnected_client_id) = disconnected_client_id_option {
            state_write.positions.remove(&disconnected_client_id);
            state_write.last_update_time.remove(&disconnected_client_id);
            state_write.last_nearby_lists.remove(&disconnected_client_id); // clean up nearby cache
            
            // Only remove the client_id -> connection_id mapping if it still points to *this* connection
            // (Avoid removing it if the client reconnected quickly and the mapping was updated)
            if state_write.client_id_to_connection_id.get(&disconnected_client_id) == Some(&disconnected_connection_id) {
                state_write.client_id_to_connection_id.remove(&disconnected_client_id);
            }
            info!("Client disconnected and cleaned up: ID {} (connection {}) ({})",
                  disconnected_client_id, disconnected_connection_id, addr);
        } else {
            // Client disconnected before sending UpdatePosition or task panicked
            info!("Unregistered connection disconnected and cleaned up: {} ({})",
                  disconnected_connection_id, addr);
        }
    }
}

// Background task to check for client timeouts
async fn check_timeouts(state: Arc<RwLock<ServerState>>) {
    let mut interval = time::interval(Duration::from_secs(5)); // Check every 5 seconds
    const TIMEOUT_DURATION: Duration = Duration::from_secs(15);

    loop {
        interval.tick().await;
        let mut timed_out_clients = Vec::new();
        let mut state_read = state.read().await; // Read lock to check times

        for (client_id, last_time) in state_read.last_update_time.iter() {
            if last_time.elapsed() > TIMEOUT_DURATION {
                info!("Client {} timed out (last update {:?})", client_id, last_time.elapsed());
                timed_out_clients.push(client_id.clone());
            }
        }
        drop(state_read); // Release read lock

        if !timed_out_clients.is_empty() {
            let mut state_write = state.write().await; // Write lock to remove
            for client_id in timed_out_clients {
                warn!("Disconnecting timed out client: {}", client_id);
                
                state_write.positions.remove(&client_id);
                state_write.last_update_time.remove(&client_id);
                state_write.last_nearby_lists.remove(&client_id); // clean up nearby cache
                
                if let Some(connection_id) = state_write.client_id_to_connection_id.remove(&client_id) {
                    // Removing the connection sender will cause the send_task for that client to terminate,
                    // eventually leading to the handle_connection task finishing and cleaning up fully.
                    state_write.connections.remove(&connection_id); 
                    info!("Removed timed out client state: {} (connection {})", client_id, connection_id);
                } else {
                    warn!("Could not find connection ID for timed out client {} during cleanup.", client_id);
                }
            }
        }
    }
}

#[tokio::main]
async fn main() {
    // Initialize logging
    env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("info")).init();

    // Create shared state
    let state = Arc::new(RwLock::new(ServerState::new()));

    // Spawn the timeout checking task
    let timeout_state = Arc::clone(&state);
    tokio::spawn(async move {
        check_timeouts(timeout_state).await;
    });

    // Create WebSocket server
    let addr = "0.0.0.0:8080";
    let listener = TcpListener::bind(&addr).await.expect("Failed to bind");
    info!("WebSocket server listening on: {}", addr);

    // Accept connections
    while let Ok((stream, addr)) = listener.accept().await {
        let state = Arc::clone(&state);
        tokio::spawn(async move {
            handle_connection(state, stream, addr).await;
        });
    }
}
