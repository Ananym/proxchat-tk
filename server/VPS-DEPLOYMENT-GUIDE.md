# VPS Deployment Guide for ProxChat Server

This guide covers deploying the ProxChat signaling server to a Linux VPS as a standalone binary.

## Building for Linux

### Option 1: Docker Extract (Recommended)

```powershell
# Build Docker image and extract Linux binary
cd server
.\build.ps1 -DockerExtract

# Creates: dist/prox-chat-server-linux
```

**This is the recommended approach** - no additional toolchain setup required.

### Option 2: Build Directly on VPS

```bash
# On your VPS
# 1. Install Rust
curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh
source ~/.cargo/env

# 2. Install git and build tools
sudo apt update
sudo apt install git build-essential

# 3. Clone and build
git clone <your-repo-url>
cd prox-chat-tk/server
cargo build --release

# 4. The binary will be at: target/release/prox-chat-server
```

## Linux Binary Naming

**Important**: Linux binaries typically have **no file extension**.

**Why no extensions?**

- Linux determines executable files by **permission bits**, not filename
- Extensions like `.exe` are a Windows convention
- Linux uses the "executable bit" (`chmod +x`) to mark files as runnable
- You can name executables anything: `server`, `my-app`, `prox-chat-server`

**Our naming convention:**

- **During development**: We use `prox-chat-server-linux` for clarity
- **On production VPS**: Rename to just `prox-chat-server`
- **Executable permission**: What makes it runnable, not the filename

```bash
# After uploading, rename for clarity
mv prox-chat-server-linux prox-chat-server

# Make executable (required on Linux)
chmod +x prox-chat-server

# Check permissions (should show 'x' for executable)
ls -la prox-chat-server
# Output: -rwxr-xr-x 1 user user 3275792 Dec 24 16:23 prox-chat-server
#           ^^^                                      (executable bits)
```

## Server Dependencies

The ProxChat server is **nearly standalone** but requires these system libraries:

### Required System Libraries (Usually Pre-installed)

- **glibc** (GNU C Library) - Standard on most Linux distros
- **libssl** - For TLS/SSL support
- **libcrypto** - Cryptographic functions

### Check Dependencies

```bash
# Check what libraries the binary needs
ldd prox-chat-server

# Example output:
# linux-vdso.so.1
# libssl.so.3 => /lib/x86_64-linux-gnu/libssl.so.3
# libcrypto.so.3 => /lib/x86_64-linux-gnu/libcrypto.so.3
# libc.so.6 => /lib/x86_64-linux-gnu/libc.so.6
```

### Install Missing Dependencies (Ubuntu/Debian)

```bash
sudo apt update
sudo apt install openssl libssl3
```

### Install Missing Dependencies (CentOS/RHEL)

```bash
sudo yum install openssl openssl-libs
# Or for newer versions:
sudo dnf install openssl openssl-libs
```

## VPS Port Configuration

### 1. Application Configuration

The server listens on port **8080** by default (hardcoded in `main.rs`):

```rust
let addr = "0.0.0.0:8080";
```

### 2. Firewall Configuration

#### Using UFW (Ubuntu/Debian)

```bash
# Enable firewall
sudo ufw enable

# Allow SSH (important!)
sudo ufw allow ssh

# Allow the ProxChat server port
sudo ufw allow 8080/tcp

# Check status
sudo ufw status
```

#### Using iptables (Manual)

```bash
# Allow incoming connections on port 8080
sudo iptables -A INPUT -p tcp --dport 8080 -j ACCEPT

# Save rules (Ubuntu/Debian)
sudo iptables-save > /etc/iptables/rules.v4

# Save rules (CentOS/RHEL)
sudo service iptables save
```

#### Using firewalld (CentOS/RHEL)

```bash
# Add the port
sudo firewall-cmd --permanent --add-port=8080/tcp

# Reload firewall
sudo firewall-cmd --reload

# Check open ports
sudo firewall-cmd --list-ports
```

### 3. VPS Provider Configuration

Most VPS providers have additional firewall controls:

#### DigitalOcean

- Go to **Networking** → **Firewalls**
- Create rule: **TCP**, **Port 8080**, **Source: All IPv4/IPv6**

#### AWS Lightsail

- Go to **Networking** tab
- Add **Custom** rule: **TCP**, **Port 8080**

#### Vultr

- Go to **Settings** → **Firewall**
- Add rule: **TCP**, **Port 8080**, **Source: Anywhere**

#### Linode

- Go to **Cloud Firewall**
- Add **Inbound** rule: **TCP**, **Port 8080**

## Deployment Steps

### 1. Upload Binary to VPS

```bash
# Using SCP (upload the -linux suffixed file)
scp dist/prox-chat-server-linux user@your-vps-ip:/home/user/

# Using SFTP
sftp user@your-vps-ip
put dist/prox-chat-server-linux
```

### 2. Make Executable and Test

```bash
# SSH into your VPS
ssh user@your-vps-ip

# Rename to standard Linux convention (no extension)
mv prox-chat-server-linux prox-chat-server

# Make executable (required on Linux)
chmod +x prox-chat-server

# Test run (foreground)
./prox-chat-server

# Should output:
# INFO - WebSocket server listening on: 0.0.0.0:8080
```

### 3. Create Systemd Service (Recommended)

Create a service file for automatic startup:

```bash
sudo nano /etc/systemd/system/prox-chat-server.service
```

Content:

```ini
[Unit]
Description=ProxChat Signaling Server
After=network.target

[Service]
Type=simple
User=your-username
WorkingDirectory=/home/your-username
ExecStart=/home/your-username/prox-chat-server
Restart=always
RestartSec=10

# Security settings
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/home/your-username

[Install]
WantedBy=multi-user.target
```

Enable and start:

```bash
# Reload systemd
sudo systemctl daemon-reload

# Enable auto-start
sudo systemctl enable prox-chat-server

# Start service
sudo systemctl start prox-chat-server

# Check status
sudo systemctl status prox-chat-server

# View logs
sudo journalctl -u prox-chat-server -f
```

## Testing the Deployment

### 1. Test Locally on VPS

```bash
# Test WebSocket connection (install websocat if needed)
echo "test" | websocat ws://localhost:8080

# Or use curl to test HTTP upgrade
curl -i -N -H "Connection: Upgrade" -H "Upgrade: websocket" \
     -H "Sec-WebSocket-Version: 13" -H "Sec-WebSocket-Key: test" \
     http://localhost:8080/
```

### 2. Test Externally

```bash
# From your development machine
echo "test" | websocat ws://your-vps-ip:8080

# Or test with your ProxChat client
# Update client config.json:
# "Host": "your-vps-ip", "Port": 8080
```

## Security Considerations

### 1. Reverse Proxy (Optional but Recommended)

Use nginx for SSL termination:

```bash
sudo apt install nginx certbot python3-certbot-nginx

# Configure nginx
sudo nano /etc/nginx/sites-available/prox-chat
```

Nginx config:

```nginx
server {
    listen 80;
    server_name your-domain.com;

    location / {
        proxy_pass http://localhost:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

Enable and get SSL:

```bash
sudo ln -s /etc/nginx/sites-available/prox-chat /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl restart nginx
sudo certbot --nginx -d your-domain.com
```

### 2. Basic Security

```bash
# Update system
sudo apt update && sudo apt upgrade

# Configure automatic updates
sudo apt install unattended-upgrades

# Set up basic fail2ban
sudo apt install fail2ban
```

## Monitoring and Maintenance

### View Logs

```bash
# Systemd logs
sudo journalctl -u prox-chat-server -f

# Check system resources
htop
```

### Update Deployment

```bash
# Stop service
sudo systemctl stop prox-chat-server

# Upload new binary
scp dist/prox-chat-server-linux user@your-vps-ip:/home/user/

# SSH in and replace binary
ssh user@your-vps-ip
mv prox-chat-server-linux prox-chat-server
chmod +x prox-chat-server

# Start service
sudo systemctl start prox-chat-server
```

## Cost Optimization

### Recommended VPS Specs

- **CPU**: 1 core (server is lightweight)
- **RAM**: 512MB - 1GB
- **Storage**: 10GB
- **Bandwidth**: 1TB/month

### Providers and Costs

- **Vultr**: $2.50-5/month
- **DigitalOcean**: $4-6/month
- **Linode**: $5/month
- **AWS Lightsail**: $3.50-5/month

### vs Container Costs

- **VPS Binary**: $3-6/month total
- **AWS Fargate**: $15-30/month
- **Savings**: 60-80% less expensive

## Troubleshooting

### "Permission denied"

```bash
chmod +x prox-chat-server
```

### "Address already in use"

```bash
# Check what's using port 8080
sudo lsof -i :8080
sudo netstat -tulpn | grep 8080

# Kill if needed
sudo kill -9 <process-id>
```

### "Connection refused"

- Check firewall rules
- Verify server is running: `sudo systemctl status prox-chat-server`
- Check VPS provider firewall settings

### Binary Dependencies Missing

```bash
# Check dependencies
ldd prox-chat-server

# Install missing libs
sudo apt install openssl libssl3
```
