# Channel System Usage

The proximity chat client now supports a channel system that allows you to separate voice chat into different logical groups.

## How Channels Work

- Only players using the same channel number will be able to hear each other
- Players on different channels will never connect to each other, even if they are in proximity
- Channel 0 is the default channel used if no channel is specified
- Channels can be any integer value (negative, zero, or positive)

## Configuring Your Channel

### Method 1: Config File

Edit your `config.json` file and set the `Channel` field:

```json
{
  "Channel": 1,
  "WebSocketServer": {
    "Host": "127.0.0.1",
    "Port": 8080
  }
  // ... other settings
}
```

### Method 2: Default Behavior

If you don't specify a channel in your config, the client will automatically use channel 0.

## Example Use Cases

- **Guild/Team Separation**: Use different channels for different guilds or teams
- **Officer Channels**: Use a separate channel for leadership discussions
- **Event Coordination**: Use dedicated channels for specific events or raids
- **Testing**: Use a test channel to avoid interfering with normal gameplay

## Channel Matching Rules

For two players to connect via proximity chat, they must meet ALL of these conditions:

1. Be on the same map/zone
2. Be within the proximity range (hardcoded to 20 units on server)
3. **Be using the same channel number**

This means you can have multiple groups of players in the same location without hearing each other if they're on different channels.
