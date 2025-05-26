# Build stage
FROM rust:latest AS builder

WORKDIR /usr/src/app

# copy cargo files first for better layer caching
COPY Cargo.toml Cargo.lock ./

# copy source code
COPY src/ ./src/

RUN cargo build --release

# Runtime stage
FROM debian:bookworm-slim

WORKDIR /usr/local/bin

# Install SSL certificates for HTTPS/WSS support
RUN apt-get update && apt-get install -y ca-certificates && rm -rf /var/lib/apt/lists/*

# Copy the built binary
COPY --from=builder /usr/src/app/target/release/prox-chat-server .

# Expose the WebSocket port
EXPOSE 8080

# Run the server
CMD ["./prox-chat-server"] 