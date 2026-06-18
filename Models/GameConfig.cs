using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace NouzertGames.Models
{
    /// <summary>
    /// Represents the local configuration for the application.
    /// Stores the AuthCode and other settings in '.system/config_auth.json'.
    /// </summary>
    public class GameConfig
    {
        /// <summary>
        /// The authentication code for RyuManifest API access.
        /// </summary>
        public string? AuthCode { get; set; }

        /// <summary>
        /// DPAPI-protected RyuManifest key. The legacy AuthCode is migrated automatically.
        /// </summary>
        public string? ProtectedAuthCode { get; set; }

        /// <summary>
        /// DPAPI-protected marker confirming activation on this computer.
        /// </summary>
        public string? LocalActivationToken { get; set; }

        /// <summary>
        /// Gets or sets the last used app ID.
        /// </summary>
        public string? LastAppId { get; set; }

        /// <summary>
        /// Gets or sets the path to the SteamTools executable.
        /// </summary>
        public string? SteamToolsPath { get; set; }

        /// <summary>
        /// Target directory for SteamTools Lua plugin files.
        /// Equivalent to RyuuManifest's steam_config_path.
        /// </summary>
        public string? SteamConfigPath { get; set; }

        /// <summary>
        /// Target directory for Steam depot manifest files.
        /// Equivalent to RyuuManifest's depot_cache_path.
        /// </summary>
        public string? DepotCachePath { get; set; }

        /// <summary>
        /// Path to Steam.exe.
        /// Equivalent to RyuuManifest's steam_exe_path.
        /// </summary>
        public string? SteamExePath { get; set; }

        /// <summary>
        /// Mirrors manifest files to Steam's root depotcache in addition to config/depotcache.
        /// Equivalent to RyuuManifest's mirror_manifests_to_steam_depotcache.
        /// </summary>
        public bool MirrorManifestsToSteamDepotcache { get; set; } = true;

        /// <summary>
        /// Gets or sets user preferences.
        /// </summary>
        public UserPreferences? Preferences { get; set; } = new();

        /// <summary>
        /// Gets or sets the list of installed games.
        /// </summary>
        public List<InstalledGame>? InstalledGames { get; set; } = new();

        /// <summary>
        /// Gets or sets the last update check timestamp.
        /// </summary>
        public DateTime? LastUpdateCheck { get; set; }
    }

    /// <summary>
    /// User preferences for the application.
    /// </summary>
    public class UserPreferences
    {
        /// <summary>
        /// Whether to automatically restart Steam after installation.
        /// </summary>
        public bool AutoRestartSteam { get; set; } = true;

        /// <summary>
        /// Whether to show notifications.
        /// </summary>
        public bool ShowNotifications { get; set; } = true;

        /// <summary>
        /// Selected theme (e.g., "Dark", "Light", "Auto").
        /// </summary>
        public string Theme { get; set; } = "Dark";

        /// <summary>
        /// Window width.
        /// </summary>
        public double WindowWidth { get; set; } = 1200;

        /// <summary>
        /// Window height.
        /// </summary>
        public double WindowHeight { get; set; } = 800;

        /// <summary>
        /// Whether window is maximized.
        /// </summary>
        public bool IsMaximized { get; set; } = false;
    }

    /// <summary>
    /// Represents an installed game in the user's library.
    /// </summary>
    public class InstalledGame
    {
        /// <summary>
        /// The application ID.
        /// </summary>
        public string AppId { get; set; } = string.Empty;

        /// <summary>
        /// The game name.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Installation date.
        /// </summary>
        public DateTime InstalledDate { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last update date.
        /// </summary>
        public DateTime? LastUpdateDate { get; set; }

        /// <summary>
        /// Whether the game is currently enabled.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Local appmanifest path when the game was discovered from Steam.
        /// </summary>
        public string? ManifestPath { get; set; }

        /// <summary>
        /// Steam build ID from the appmanifest file.
        /// </summary>
        public string? BuildId { get; set; }

        /// <summary>
        /// Size on disk in bytes, when available in the appmanifest file.
        /// </summary>
        public long SizeOnDisk { get; set; }
    }

    /// <summary>
    /// Service for managing game configuration persistence.
    /// Stores configuration in the .system hidden folder.
    /// </summary>
    public class ConfigService
    {
        private readonly string _configFilePath;
        private GameConfig? _cachedConfig;

        private const string ConfigFileName = "config_auth.json";

        public ConfigService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var systemFolder = Path.Combine(appData, "NouzertGames", ".system");
            
            EnsureHiddenFolder(systemFolder);
            _configFilePath = Path.Combine(systemFolder, ConfigFileName);
        }

        private static void EnsureHiddenFolder(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            File.SetAttributes(path, FileAttributes.Hidden);
        }

        /// <summary>
        /// Loads the configuration from the JSON file.
        /// If the file doesn't exist, creates a new default configuration.
        /// </summary>
        /// <returns>The loaded or newly created GameConfig</returns>
        public GameConfig LoadConfig()
        {
            if (_cachedConfig != null)
                return _cachedConfig;

            try
            {
                if (File.Exists(_configFilePath))
                {
                    var json = File.ReadAllText(_configFilePath);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        WriteIndented = true
                    };
                    
                    _cachedConfig = JsonSerializer.Deserialize<GameConfig>(json, options);
                    
                    if (_cachedConfig == null)
                    {
                        _cachedConfig = CreateDefaultConfig();
                    }
                }
                else
                {
                    _cachedConfig = CreateDefaultConfig();
                    SaveConfig(_cachedConfig);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Erro crítico ao carregar config: {ex.Message}");
                // If loading fails, create a new default config
                _cachedConfig = CreateDefaultConfig();
            }

            return _cachedConfig;
        }

        /// <summary>
        /// Saves the configuration to the JSON file.
        /// </summary>
        /// <param name="config">The configuration to save</param>
        public void SaveConfig(GameConfig config)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                
                var json = JsonSerializer.Serialize(config, options);
                File.WriteAllText(_configFilePath, json);
                _cachedConfig = config;
            }
            catch (Exception ex)
            {
                // Recomenda-se logar o erro para diagnóstico
                System.Diagnostics.Debug.WriteLine($"Erro ao salvar configuração: {ex.Message}");
            }
        }

        /// <summary>
        /// Asynchronously loads the configuration.
        /// </summary>
        public async Task<GameConfig> LoadConfigAsync()
        {
            if (_cachedConfig != null)
                return _cachedConfig;

            try
            {
                if (File.Exists(_configFilePath))
                {
                    var json = await File.ReadAllTextAsync(_configFilePath);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        WriteIndented = true
                    };
                    
                    _cachedConfig = JsonSerializer.Deserialize<GameConfig>(json, options);
                    
                    if (_cachedConfig == null)
                    {
                        _cachedConfig = CreateDefaultConfig();
                    }
                }
                else
                {
                    _cachedConfig = CreateDefaultConfig();
                    await SaveConfigAsync(_cachedConfig);
                }
            }
            catch (Exception)
            {
                _cachedConfig = CreateDefaultConfig();
            }

            return _cachedConfig;
        }

        /// <summary>
        /// Asynchronously saves the configuration.
        /// </summary>
        public async Task SaveConfigAsync(GameConfig config)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                
                var json = JsonSerializer.Serialize(config, options);
                await File.WriteAllTextAsync(_configFilePath, json);
                _cachedConfig = config;
            }
            catch (Exception ex)
            {
                // Logar o erro é essencial para debugar falhas de escrita
                System.Diagnostics.Debug.WriteLine($"Falha no salvamento assíncrono: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates only the AuthCode in the configuration.
        /// </summary>
        /// <param name="authCode">The new auth code</param>
        public void UpdateAuthCode(string authCode)
        {
            var config = LoadConfig();
            config.AuthCode = authCode;
            SaveConfig(config);
        }

        /// <summary>
        /// Gets the current AuthCode.
        /// </summary>
        public string? GetAuthCode()
        {
            var config = LoadConfig();
            return config.AuthCode;
        }

        /// <summary>
        /// Adds a game to the installed games list.
        /// </summary>
        public void AddInstalledGame(string appId, string? name = null)
        {
            var config = LoadConfig();
            
            if (config.InstalledGames == null)
                config.InstalledGames = new List<InstalledGame>();

            // Check if already exists
            var existing = config.InstalledGames.FirstOrDefault(g => g.AppId == appId);
            if (existing != null)
            {
                existing.IsEnabled = true;
                existing.LastUpdateDate = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(name))
                    existing.Name = name;
            }
            else
            {
                config.InstalledGames.Add(new InstalledGame
                {
                    AppId = appId,
                    Name = name,
                    InstalledDate = DateTime.UtcNow
                });
            }

            SaveConfig(config);
        }

        /// <summary>
        /// Removes a game from the installed games list.
        /// </summary>
        public void RemoveInstalledGame(string appId)
        {
            var config = LoadConfig();
            
            if (config.InstalledGames == null)
                return;

            var game = config.InstalledGames.FirstOrDefault(g => g.AppId == appId);
            if (game != null)
            {
                config.InstalledGames.Remove(game);
                SaveConfig(config);
            }
        }

        /// <summary>
        /// Checks if a game is installed.
        /// </summary>
        public bool IsGameInstalled(string appId)
        {
            var config = LoadConfig();
            return config.InstalledGames?.Any(g => g.AppId == appId) ?? false;
        }

        private GameConfig CreateDefaultConfig()
        {
            return new GameConfig
            {
                AuthCode = null,
                ProtectedAuthCode = null,
                LocalActivationToken = null,
                LastAppId = null,
                SteamToolsPath = null,
                SteamConfigPath = null,
                DepotCachePath = null,
                SteamExePath = null,
                MirrorManifestsToSteamDepotcache = true,
                Preferences = new UserPreferences(),
                InstalledGames = new List<InstalledGame>(),
                LastUpdateCheck = null
            };
        }

        /// <summary>
        /// Gets the configuration file path.
        /// </summary>
        public string GetConfigFilePath() => _configFilePath;

        /// <summary>
        /// Updates the SteamTools executable path in the configuration.
        /// </summary>
        /// <param name="steamToolsPath">The path to the SteamTools executable</param>
        public void UpdateSteamToolsPath(string steamToolsPath)
        {
            var config = LoadConfig();
            config.SteamToolsPath = steamToolsPath;
            SaveConfig(config);
        }
    }
}
