using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using ProxChatClient.Models;

namespace ProxChatClient.Services;

public class ConfigService
{
    private static readonly string ConfigFileName = "config.json";
    private Config _config;
    
    public Config Config => _config;

    public ConfigService()
    {
        _config = LoadConfig();
    }

    /// <summary>
    /// gets the directory where persistent config files should be stored.
    /// for velopack apps, stores one level up from 'current' to survive updates
    /// </summary>
    public static string GetConfigDirectory()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string dirName = Path.GetFileName(baseDir.TrimEnd(Path.DirectorySeparatorChar));
        
        // check if we're running from a velopack 'current' directory
        if (dirName == "current")
        {
            // remove trailing slash before getting parent directory
            string cleanBaseDir = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parentInfo = Directory.GetParent(cleanBaseDir);
            if (parentInfo != null)
            {
                string parentDir = parentInfo.FullName;
                return parentDir;
            }
            else
            {
                return baseDir;
            }
        }
        
        return baseDir;
    }

    public static string GetConfigFilePath() => Path.Combine(GetConfigDirectory(), ConfigFileName);

    private Config LoadConfig()
    {
        try
        {
            string configPath = GetConfigFilePath();
            
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<Config>(json);
                if (config != null) return config;

                Trace.TraceWarning($"config.json found at {configPath} but could not be deserialized properly. Using default configuration.");
            }
            else
            {
                Trace.TraceWarning($"config.json not found at {configPath}. Using default configuration and creating a new one.");
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Error loading config.json: {ex.Message}. Using default configuration.");
        }
        
        // create default config and save it
        var defaultConfig = new Config();
        SaveConfig(defaultConfig);
        return defaultConfig;
    }

    public void SaveConfig() => SaveConfig(_config);

    private static void SaveConfig(Config config)
    {
        try 
        { 
            string configPath = GetConfigFilePath();
            string json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(configPath, json);
            Trace.TraceInformation($"Saved config to {configPath}");
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Failed to save config.json: {ex.Message}");
        }
    }

    public void UpdatePeerSetting(string characterName, float volume, bool isMuted)
    {
        if (string.IsNullOrEmpty(characterName)) return;

        bool changed = false;
        if (_config.PeerSettings.TryGetValue(characterName, out var existingSettings))
        {
            // check if anything actually changed
            if (Math.Abs(existingSettings.Volume - volume) > 0.001f || existingSettings.IsMuted != isMuted)
            {
                existingSettings.Volume = volume;
                existingSettings.IsMuted = isMuted;
                changed = true;
            }
        }
        else
        {
            // new entry, always save
            _config.PeerSettings[characterName] = new PeerPersistentState { Volume = volume, IsMuted = isMuted };
            changed = true;
        }

        if (changed)
        {
            SaveConfig();
            Debug.WriteLine($"Updated peer setting for {characterName}: Vol={volume}, Mute={isMuted}");
        }
    }
} 