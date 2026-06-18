using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NouzertGames.Services;
using NouzertGames.Models;

namespace NouzertGames.Services
{
    /// <summary>
    /// Service responsible for Steam-related operations including:
    /// - Reading Steam installation path from Windows Registry
    /// - Creating required directories for manifest and depotcache
    /// - Injecting manifest and Lua files
    /// - Restarting Steam process
    /// </summary>
    public class SteamService
    {
        private readonly LoggerService _logger;
        private string? _steamPath;
        private string? _steamExecutablePath;

        private const string SteamRegistryPath = @"Software\Valve\Steam";
        private const string SteamPathKey = "SteamPath";
        private const string SteamExeName = "Steam.exe";

        public SteamService(LoggerService logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gets the Steam installation path from Windows Registry.
        /// Reads from HKEY_CURRENT_USER\Software\Valve\Steam\SteamPath
        /// </summary>
        /// <returns>The Steam path if found, null otherwise</returns>
        public string? GetSteamPath()
        {
            if (!string.IsNullOrEmpty(_steamPath))
                return _steamPath;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SteamRegistryPath);
                if (key != null)
                {
                    var steamPath = key.GetValue(SteamPathKey) as string;
                    if (!string.IsNullOrEmpty(steamPath))
                    {
                        _steamPath = steamPath;
                        _logger.Info($"Steam path detected: {steamPath}");
                        return steamPath;
                    }
                }
                
                _logger.Warning("Steam path not found in registry");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to read Steam path from registry", ex);
                return null;
            }
        }

        /// <summary>
        /// Gets the full path to the Steam executable.
        /// </summary>
        /// <returns>Full path to Steam.exe if found, null otherwise</returns>
        public string? GetSteamExecutablePath()
        {
            if (!string.IsNullOrEmpty(_steamExecutablePath) && File.Exists(_steamExecutablePath))
                return _steamExecutablePath;

            var steamPath = GetSteamPath();
            if (string.IsNullOrEmpty(steamPath))
                return null;

            _steamExecutablePath = Path.Combine(steamPath, SteamExeName);
            
            if (!File.Exists(_steamExecutablePath))
            {
                _logger.Warning($"Steam executable not found at: {_steamExecutablePath}");
                return null;
            }

            _logger.Info($"Steam executable found: {_steamExecutablePath}");
            return _steamExecutablePath;
        }

        /// <summary>
        /// Gets the path to the stplug-in directory, creating it if necessary.
        /// Path: [SteamPath]\config\stplug-in
        /// </summary>
        /// <returns>Full path to the stplug-in directory</returns>
        public string? GetStPlugInDirectory()
        {
            var steamPath = GetSteamPath();
            if (string.IsNullOrEmpty(steamPath))
                return null;

            var stPlugInPath = Path.Combine(steamPath, "config", "stplug-in");
            
            try
            {
                if (!Directory.Exists(stPlugInPath))
                {
                    Directory.CreateDirectory(stPlugInPath);
                    _logger.Info($"Created stplug-in directory: {stPlugInPath}");
                }
                return stPlugInPath;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create stplug-in directory: {stPlugInPath}", ex);
                return null;
            }
        }

        /// <summary>
        /// Gets the path to the depotcache directory, creating it if necessary.
        /// Path: [SteamPath]\config\depotcache
        /// </summary>
        /// <returns>Full path to the depotcache directory</returns>
        public string? GetDepotCacheDirectory()
        {
            var steamPath = GetSteamPath();
            if (string.IsNullOrEmpty(steamPath))
                return null;

            var depotCachePath = Path.Combine(steamPath, "config", "depotcache");
            
            try
            {
                if (!Directory.Exists(depotCachePath))
                {
                    Directory.CreateDirectory(depotCachePath);
                    _logger.Info($"Created depotcache directory: {depotCachePath}");
                }
                return depotCachePath;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create depotcache directory: {depotCachePath}", ex);
                return null;
            }
        }

        /// <summary>
        /// Saves a manifest file for a specific appid.
        /// Path: [SteamPath]\config\depotcache\{appid}.manifest
        /// </summary>
        /// <param name="appId">The application ID</param>
        /// <param name="manifestContent">The manifest file content</param>
        /// <returns>True if successful, false otherwise</returns>
        public bool SaveManifestFile(string appId, byte[] manifestContent)
        {
            var depotCachePath = GetDepotCacheDirectory();
            if (string.IsNullOrEmpty(depotCachePath))
                return false;

            var manifestPath = Path.Combine(depotCachePath, $"{appId}.manifest");
            
            try
            {
                File.WriteAllBytes(manifestPath, manifestContent);
                _logger.Info($"Manifest saved: {manifestPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save manifest file: {manifestPath}", ex);
                return false;
            }
        }

        /// <summary>
        /// Saves a Lua script file for a specific appid.
        /// Path: [SteamPath]\config\stplug-in\{appid}.lua
        /// </summary>
        /// <param name="appId">The application ID</param>
        /// <param name="luaContent">The Lua script content</param>
        /// <returns>True if successful, false otherwise</returns>
        public bool SaveLuaFile(string appId, string luaContent)
        {
            var stPlugInPath = GetStPlugInDirectory();
            if (string.IsNullOrEmpty(stPlugInPath))
                return false;

            var luaPath = Path.Combine(stPlugInPath, $"{appId}.lua");
            
            try
            {
                File.WriteAllText(luaPath, luaContent);
                _logger.Info($"Lua file saved: {luaPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save Lua file: {luaPath}", ex);
                return false;
            }
        }

        /// <summary>
        /// Kills the Steam process if it's running.
        /// Uses: taskkill /F /IM steam.exe
        /// </summary>
        /// <returns>True if Steam was killed or not running, false if kill failed</returns>
        public bool KillSteamProcess()
        {
            try
            {
                var processes = Process.GetProcessesByName("steam");
                if (processes.Length == 0)
                {
                    _logger.Info("Steam process is not running");
                    return true;
                }

                foreach (var process in processes)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(5000); // Wait up to 5 seconds
                        _logger.Info("Steam process killed successfully");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to kill Steam process (PID: {process.Id}): {ex.Message}");
                    }
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to kill Steam process", ex);
                return false;
            }
        }

        /// <summary>
        /// Starts the Steam process using the detected executable path.
        /// </summary>
        /// <returns>True if Steam started successfully, false otherwise</returns>
        public bool StartSteamProcess()
        {
            var steamPath = GetSteamExecutablePath();
            if (string.IsNullOrEmpty(steamPath) || !File.Exists(steamPath))
            {
                _logger.Error("Cannot start Steam: executable not found");
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = steamPath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(steamPath)
                };

                Process.Start(startInfo);
                _logger.Info($"Steam process started: {steamPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start Steam process: {steamPath}", ex);
                return false;
            }
        }

        /// <summary>
        /// Restarts the Steam process synchronously (kill and start).
        /// </summary>
        /// <returns>True if restart was successful, false otherwise</returns>
        public bool RestartSteam()
        {
            _logger.Info("Restarting Steam...");

            if (!KillSteamProcess())
            {
                _logger.Warning("Failed to kill Steam process during restart");
                return false;
            }

            // Wait a moment for Steam to fully terminate
            System.Threading.Thread.Sleep(2000);

            return StartSteamProcess();
        }

        /// <summary>
        /// Restarts the Steam process asynchronously (kill and start).
        /// </summary>
        /// <returns>True if restart was successful, false otherwise</returns>
        public async Task<bool> RestartSteamAsync()
        {
            _logger.Info("Restarting Steam...");
            
            if (!KillSteamProcess())
            {
                _logger.Warning("Failed to kill Steam process during restart");
                return false;
            }

            // Wait a moment for Steam to fully terminate without blocking the UI
            await Task.Delay(2000);

            return StartSteamProcess();
        }

        /// <summary>
        /// Checks if Steam is currently running.
        /// </summary>
        /// <returns>True if Steam is running, false otherwise</returns>
        public bool IsSteamRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName("steam");
                return processes.Length > 0;
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to check if Steam is running", ex);
                return false;
            }
        }

        /// <summary>
        /// Reads Steam library folders and appmanifest_*.acf files to find installed games.
        /// </summary>
        public List<InstalledGame> GetInstalledSteamGames()
        {
            var steamPath = GetSteamPath();
            if (string.IsNullOrEmpty(steamPath))
                return new List<InstalledGame>();

            var libraries = GetSteamLibraryPaths(steamPath);
            var games = new List<InstalledGame>();

            foreach (var libraryPath in libraries)
            {
                var steamAppsPath = Path.Combine(libraryPath, "steamapps");
                if (!Directory.Exists(steamAppsPath))
                    continue;

                foreach (var manifestPath in Directory.EnumerateFiles(steamAppsPath, "appmanifest_*.acf"))
                {
                    var game = TryReadAppManifest(manifestPath);
                    if (game != null)
                        games.Add(game);
                }
            }

            return games
                .GroupBy(game => game.AppId)
                .Select(group => group.First())
                .OrderBy(game => game.Name ?? game.AppId)
                .ToList();
        }

        private List<string> GetSteamLibraryPaths(string steamPath)
        {
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                steamPath
            };

            var libraryFoldersPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFoldersPath))
                return libraries.ToList();

            try
            {
                var content = File.ReadAllText(libraryFoldersPath);

                foreach (Match match in Regex.Matches(content, "\"path\"\\s+\"(?<path>[^\"]+)\""))
                {
                    var path = match.Groups["path"].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(path))
                        libraries.Add(path);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to read Steam library folders: {ex.Message}");
            }

            return libraries.ToList();
        }

        private InstalledGame? TryReadAppManifest(string manifestPath)
        {
            try
            {
                var content = File.ReadAllText(manifestPath);
                var appId = ReadAcfValue(content, "appid");

                if (string.IsNullOrWhiteSpace(appId))
                    appId = Path.GetFileNameWithoutExtension(manifestPath).Replace("appmanifest_", string.Empty);

                if (string.IsNullOrWhiteSpace(appId))
                    return null;

                var name = ReadAcfValue(content, "name");
                var buildId = ReadAcfValue(content, "buildid");
                var sizeOnDisk = ReadAcfValue(content, "SizeOnDisk");

                return new InstalledGame
                {
                    AppId = appId,
                    Name = string.IsNullOrWhiteSpace(name) ? $"App {appId}" : name,
                    IsEnabled = true,
                    ManifestPath = manifestPath,
                    BuildId = buildId,
                    SizeOnDisk = long.TryParse(sizeOnDisk, out var size) ? size : 0
                };
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to read app manifest '{manifestPath}': {ex.Message}");
                return null;
            }
        }

        private static string? ReadAcfValue(string content, string key)
        {
            var match = Regex.Match(content, $"\"{Regex.Escape(key)}\"\\s+\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["value"].Value : null;
        }

        /// <summary>
        /// Installs a game by downloading and saving manifest and Lua files.
        /// </summary>
        /// <param name="appId">The application ID</param>
        /// <param name="manifestContent">The manifest content</param>
        /// <param name="luaContent">The Lua content</param>
        /// <returns>True if installation was successful, false otherwise</returns>
        public bool InstallGame(string appId, byte[] manifestContent, string luaContent)
        {
            _logger.Info($"Installing game: {appId}");
            CleanupLegacySwappedFiles(appId);

            // Save manifest
            if (!SaveManifestFile(appId, manifestContent))
            {
                _logger.Error($"Failed to save manifest for app: {appId}");
                return false;
            }

            // Save Lua
            if (!SaveLuaFile(appId, luaContent))
            {
                _logger.Error($"Failed to save Lua for app: {appId}");
                // Try to clean up manifest if Lua failed
                try
                {
                    var depotCachePath = GetDepotCacheDirectory();
                    if (!string.IsNullOrEmpty(depotCachePath))
                    {
                        var manifestPath = Path.Combine(depotCachePath, $"{appId}.manifest");
                        if (File.Exists(manifestPath))
                            File.Delete(manifestPath);
                    }
                }
                catch { }
                return false;
            }

            _logger.Info($"Game {appId} installed successfully");
            return true;
        }

        private void CleanupLegacySwappedFiles(string appId)
        {
            try
            {
                var stPlugInPath = GetStPlugInDirectory();
                var depotCachePath = GetDepotCacheDirectory();

                var oldManifestPath = !string.IsNullOrEmpty(stPlugInPath)
                    ? Path.Combine(stPlugInPath, $"{appId}.manifest")
                    : null;
                var oldLuaPath = !string.IsNullOrEmpty(depotCachePath)
                    ? Path.Combine(depotCachePath, $"{appId}.lua")
                    : null;

                if (!string.IsNullOrEmpty(oldManifestPath) && File.Exists(oldManifestPath))
                {
                    File.Delete(oldManifestPath);
                    _logger.Info($"Removed legacy misplaced manifest: {oldManifestPath}");
                }

                if (!string.IsNullOrEmpty(oldLuaPath) && File.Exists(oldLuaPath))
                {
                    File.Delete(oldLuaPath);
                    _logger.Info($"Removed legacy misplaced Lua: {oldLuaPath}");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to clean legacy swapped files for app {appId}: {ex.Message}");
            }
        }
    }
}
