using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization; // For attributes if needed

namespace ProxChatClient.Services;

// Helper classes for JSON deserialization
internal class PlayerData
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("mapId")]
    public int MapId { get; set; } // Changed from string to int

    [JsonPropertyName("mapName")] // Added MapName property
    public string MapName { get; set; } = string.Empty; // Initialize to avoid null

    [JsonPropertyName("characterName")]
    public string CharacterName { get; set; } = "Player"; // Initialize to avoid null
}

internal class GameData
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; } // ISO timestamp from memory reader

    [JsonPropertyName("data")]
    public PlayerData? Data { get; set; } // Nullable in case success is false or data is missing
}


public class GameDataReader : IDisposable // Renamed class
{
    private const string MMF_NAME = "NexusTKMemoryData";
    private const int MMF_SIZE = 1024; // Use the provided size
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private bool _disposed = false; // To detect redundant calls

    // Add event for new game data
    public event EventHandler<(bool Success, int MapId, string MapName, int X, int Y, string CharacterName)>? GameDataRead;

    public GameDataReader() // Renamed constructor
    {
        // Constructor is intentionally kept light. Opening MMF is deferred.
    }

    // Ensures the MMF is open and the accessor is created.
    // Returns true if ready, false otherwise.
    private bool EnsureMmfOpen()
    {
        if (_disposed)
        {
            Debug.WriteLine("Attempted to use disposed GameDataReader."); // Updated message
            return false;
        }
        if (_accessor != null && _mmf != null) return true; // Already open

        try
        {
            // Dispose previous resources if any (e.g., if EnsureMmfOpen failed previously)
            _accessor?.Dispose();
            _mmf?.Dispose();

            _mmf = MemoryMappedFile.OpenExisting(MMF_NAME);
            _accessor = _mmf.CreateViewAccessor(0, MMF_SIZE, MemoryMappedFileAccess.Read);
            Debug.WriteLine($"Successfully opened MMF: {MMF_NAME}");
            return true;
        }
        catch (FileNotFoundException)
        {
            // This is expected if the provider process isn't running yet.
            // Log less verbosely or only on first occurrence if needed.
             Debug.WriteLine($"Memory-mapped file not found: {MMF_NAME}. Waiting for provider process...");
            _accessor = null;
            _mmf = null;
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error opening memory-mapped file {MMF_NAME}: {ex.Message}");
            _accessor?.Dispose(); // Ensure cleanup on failure
            _mmf?.Dispose();
            _accessor = null;
            _mmf = null;
            return false;
        }
    }


    // Main method used by ViewModel to read the current game state.
    public (bool Success, int MapId, string MapName, int X, int Y, string CharacterName) ReadPositionAndName()
    {
         // Default values returned on failure or if MMF not ready
        int mapId = 0; // Internal representation is int
        string mapName = string.Empty; // Added default for mapName
        int x = 0;
        int y = 0;
        string characterName = "Player"; // Default name
        bool success = false;

        if (!EnsureMmfOpen() || _accessor == null)
        {
            // MMF couldn't be opened or accessor is null, return defaults
            return (success, mapId, mapName, x, y, characterName); // Return int mapId
        }

        try
        {
            byte[] buffer = new byte[MMF_SIZE];
            // Read the entire buffer size specified for the MMF
            _accessor.ReadArray(0, buffer, 0, MMF_SIZE);

            // Find the first null terminator, as C++ likely null-terminates the string
            int length = Array.IndexOf(buffer, (byte)0);
            if (length == -1)
            {
                 // If no null terminator, maybe the buffer is full?
                 // Or maybe it's just not null-terminated. Assume full length.
                 // Log this case if it's unexpected.
                 Debug.WriteLine($"No null terminator found in MMF buffer. Reading full {MMF_SIZE} bytes.");
                 length = MMF_SIZE;
            }

            // Convert the relevant part of the buffer (up to null terminator or full size) to a string
            string jsonString = Encoding.UTF8.GetString(buffer, 0, length).Trim();

            // Handle cases where the buffer might be empty or contain only whitespace/nulls
            if (string.IsNullOrWhiteSpace(jsonString))
            {
                // This might happen if the provider hasn't written data yet
                // Or if the data is genuinely empty. Avoid spamming logs if frequent.
                // Debug.WriteLine("MMF contains no readable data or is empty.");
                return (success, mapId, mapName, x, y, characterName); // Return defaults with int mapId
            }

            // Deserialize the JSON string using System.Text.Json
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true // More robust against case variations
            };
            GameData? gameData = JsonSerializer.Deserialize<GameData>(jsonString, options);

            if (gameData != null && gameData.Success && gameData.Data != null)
            {
                // check timestamp validity (must be within 10 seconds)
                bool timestampValid = true;
                if (!string.IsNullOrEmpty(gameData.Timestamp))
                {
                    try
                    {
                        var timestamp = DateTime.Parse(gameData.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind);
                        var age = DateTime.UtcNow - timestamp;
                        if (age.TotalSeconds > 10.0)
                        {
                            timestampValid = false;
                            Debug.WriteLine($"Game data timestamp too old: {age.TotalSeconds:F1}s ago");
                        }
                    }
                    catch (Exception ex)
                    {
                        timestampValid = false;
                        Debug.WriteLine($"Failed to parse timestamp '{gameData.Timestamp}': {ex.Message}");
                    }
                }
                else
                {
                    timestampValid = false;
                    Debug.WriteLine("Game data missing timestamp");
                }

                if (timestampValid)
                {
                    // Successfully parsed and success flag is true with valid timestamp
                    success = true;
                    mapId = gameData.Data.MapId; // Now an int
                    mapName = gameData.Data.MapName ?? string.Empty; // Added mapName reading
                    x = gameData.Data.X;
                    y = gameData.Data.Y;
                    characterName = gameData.Data.CharacterName ?? "Player"; // Use default if null

                    // Raise event with new data
                    GameDataRead?.Invoke(this, (success, mapId, mapName, x, y, characterName));

                    // Optional: Verbose logging for successful reads
                    // Debug.WriteLine($"Read data: MapId={mapId}, MapName='{mapName}', X={x}, Y={y}, Name='{characterName}'");
                }
                else
                {
                    // timestamp validation failed, treat as unsuccessful read
                    success = false;
                }
            }
            else if (gameData == null)
            {
                 Debug.WriteLine($"Failed to deserialize JSON from MMF. Raw JSON (check length): '{jsonString.Substring(0, Math.Min(jsonString.Length, 100))}'"); // Log truncated raw string
            }
            else if (!gameData.Success)
            {
                 // Log if the provider indicated failure
                 // Debug.WriteLine("MMF data indicates success=false.");
            }
            // Implicitly return defaults if gameData.Data is null even if gameData.Success is true
        }
        catch (JsonException jsonEx)
        {
            Debug.WriteLine($"Error deserializing JSON from MMF: {jsonEx.Message}.");
             // Optionally log the raw string fragment during debugging, be mindful of PII or large data
             // byte[] tempBuffer = new byte[MMF_SIZE]; _accessor.ReadArray(0, tempBuffer, 0, MMF_SIZE);
             // int tempLength = Array.IndexOf(tempBuffer, (byte)0); if(tempLength == -1) tempLength = MMF_SIZE;
             // string rawStr = Encoding.UTF8.GetString(tempBuffer, 0, tempLength);
             // Debug.WriteLine($"Raw MMF String causing error (truncated): '{rawStr.Substring(0, Math.Min(rawStr.Length, 100))}'");
        }
        catch (Exception ex)
        {
            // Catch broader exceptions related to MMF access after opening
            Debug.WriteLine($"Error reading from memory-mapped file {MMF_NAME}: {ex.Message}");
            // Consider closing/disposing MMF resources if error is persistent or critical
            Close(); // Close MMF on read error to attempt reopening next time
        }

        // Return the read values, using int mapId
        return (success, mapId, mapName, x, y, characterName); 
    }

    // Explicitly close and dispose MMF resources
    public void Close()
    {
        Dispose(true); // Dispose managed and unmanaged resources
        GC.SuppressFinalize(this); // Prevent finalizer from running
    }

    // Protected virtual method for IDisposable pattern
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Dispose managed state (managed objects).
                _accessor?.Dispose();
                _mmf?.Dispose();
                 Debug.WriteLine($"Disposed MMF resources for: {MMF_NAME}");
            }

            // Free unmanaged resources (unmanaged objects) and override a finalizer below.
            // Set large fields to null.
            _accessor = null;
            _mmf = null;
            _disposed = true;
        }
    }

    // Implement IDisposable interface
    public void Dispose()
    {
        Close(); // Calls Dispose(true) and GC.SuppressFinalize
    }

    // Optional: Finalizer in case Dispose is not called explicitly
     ~GameDataReader() // Renamed finalizer
     {
         // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
         Dispose(disposing: false);
     }
} 