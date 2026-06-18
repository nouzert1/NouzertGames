using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NouzertGames.Models;

namespace NouzertGames.Services
{
    /// <summary>
    /// Service for interacting with the RyuManifest API.
    /// Handles all HTTP requests for manifest, Lua, and game management operations.
    /// The reseller auth code is intentionally not embedded in the executable.
    /// Configure it locally or proxy these requests through a server you control.
    /// </summary>
    public class RyuApiService
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        private static readonly SemaphoreSlim _steamDetailsLimiter = new SemaphoreSlim(6);
        private static List<GameItem>? _steamAppListCache;
        private static DateTime _steamAppListCacheTime;
        private readonly LoggerService _logger;

        public string AuthCode { get; set; } = string.Empty;
        private const string Source = "neighbor";
        private const string BaseUrl = "https://generator.ryuu.lol";
        private const string RawgApiKey = "9340af08960048b591ce75f5c96485ae";
        private const string SteamAppListUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";
        private const string SteamFeaturedCategoriesUrl = "https://store.steampowered.com/api/featuredcategories?cc=br&l=brazilian";
        private const string SteamStoreSearchUrl = "https://store.steampowered.com/api/storesearch/";

        /// <summary>
        /// Initializes a new instance of the RyuApiService.
        /// </summary>
        /// <param name="logger">Logger service for recording API operations</param>
        public RyuApiService(LoggerService logger)
        {
            _logger = logger;
            
            // Set default headers
            if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "NouzertTools/1.0");
        }

        /// <summary>
        /// Builds the API URL for a specific endpoint.
        /// </summary>
        private string BuildUrl(string endpoint, string appId)
        {
            if (string.IsNullOrWhiteSpace(AuthCode))
                throw new InvalidOperationException("RyuManifest API key is not configured.");

            return BuildUrl(endpoint, appId, AuthCode);
        }

        private static string BuildUrl(string endpoint, string appId, string authCode)
        {
            var escapedAppId = Uri.EscapeDataString(appId);
            var escapedAuth = Uri.EscapeDataString(authCode);
            var escapedSource = Uri.EscapeDataString(Source);
            return $"{BaseUrl}{endpoint}?appid={escapedAppId}&auth_code={escapedAuth}&source={escapedSource}";
        }

        public async Task<bool> ValidateAuthCodeAsync(string authCode)
        {
            if (string.IsNullOrWhiteSpace(authCode))
                return false;

            try
            {
                var url = BuildUrl("/resellerlua", "500", authCode.Trim());
                _logger.Info("Validating RyuManifest reseller key...");

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warning($"RyuManifest key validation failed. Status: {response.StatusCode}");
                    return false;
                }

                var content = await response.Content.ReadAsStringAsync();
                var valid = !string.IsNullOrWhiteSpace(content)
                    && content.Contains("addappid", StringComparison.OrdinalIgnoreCase);

                _logger.Info(valid
                    ? "RyuManifest reseller key validated successfully"
                    : "RyuManifest key validation returned an invalid payload");

                return valid;
            }
            catch (Exception ex)
            {
                _logger.Error("RyuManifest key validation failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Downloads the manifest file for a specific appid.
        /// Endpoint: /secure_download
        /// Save to: [SteamPath]\config\depotcache\{appid}.manifest
        /// </summary>
        /// <param name="appId">The application ID</param>
        /// <returns>The manifest content as bytes, or null if failed</returns>
        public async Task<byte[]?> DownloadManifestAsync(string appId)
        {
            try
            {
                var url = BuildUrl("/secure_download", appId);
                _logger.Info($"Downloading manifest for app {appId}...");
                _logger.Debug($"URL: {url}");
                
                var response = await _httpClient.GetAsync(url);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsByteArrayAsync();
                    if (content.Length < 32)
                    {
                        _logger.Error($"Manifest download for app {appId} returned an invalid small payload ({content.Length} bytes)");
                        return null;
                    }

                    _logger.Info($"Manifest downloaded successfully for app {appId} ({content.Length} bytes)");
                    return content;
                }
                else
                {
                    _logger.Error($"Failed to download manifest for app {appId}. Status: {response.StatusCode}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception while downloading manifest for app {appId}", ex);
                return null;
            }
        }

        /// <summary>
        /// Downloads the Lua script for a specific appid.
        /// Endpoint: /resellerlua
        /// Save to: [SteamPath]\config\stplug-in\{appid}.lua
        /// </summary>
        /// <param name="appId">The application ID</param>
        /// <returns>The Lua content as a string, or null if failed</returns>
        public async Task<string?> DownloadLuaAsync(string appId)
        {
            try
            {
                var url = BuildUrl("/resellerlua", appId);
                _logger.Info($"Downloading Lua script for app {appId}...");
                _logger.Debug($"URL: {url}");
                
                var response = await _httpClient.GetAsync(url);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    if (string.IsNullOrWhiteSpace(content) || !content.Contains("addappid", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Error($"Lua script for app {appId} is invalid or empty");
                        return null;
                    }

                    _logger.Info($"Lua script downloaded successfully for app {appId} ({content.Length} chars)");
                    return content;
                }
                else
                {
                    _logger.Error($"Failed to download Lua script for app {appId}. Status: {response.StatusCode}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception while downloading Lua script for app {appId}", ex);
                return null;
            }
        }

        /// <summary>
        /// Requests an update for a specific appid.
        /// Endpoint: /resellerrequestupdate
        /// </summary>
        /// <param name="appId">The application ID</param>
        /// <returns>True if the update request was successful, false otherwise</returns>
        public async Task<bool> RequestUpdateAsync(string appId)
        {
            try
            {
                var url = BuildUrl("/resellerrequestupdate", appId);
                _logger.Info($"Requesting update for app {appId}...");
                _logger.Debug($"URL: {url}");
                
                var response = await _httpClient.GetAsync(url);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    _logger.Info($"Update requested successfully for app {appId}. Response: {content}");
                    return true;
                }
                else
                {
                    _logger.Error($"Failed to request update for app {appId}. Status: {response.StatusCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception while requesting update for app {appId}", ex);
                return false;
            }
        }

        /// <summary>
        /// Requests a game for a specific appid.
        /// Endpoint: /resellerrequest
        /// </summary>
        /// <param name="appId">The application ID</param>
        /// <returns>True if the game request was successful, false otherwise</returns>
        public async Task<bool> RequestGameAsync(string appId)
        {
            try
            {
                var url = BuildUrl("/resellerrequest", appId);
                _logger.Info($"Requesting game for app {appId}...");
                _logger.Debug($"URL: {url}");
                
                var response = await _httpClient.GetAsync(url);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    _logger.Info($"Game requested successfully for app {appId}. Response: {content}");
                    return true;
                }
                else
                {
                    _logger.Error($"Failed to request game for app {appId}. Status: {response.StatusCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception while requesting game for app {appId}", ex);
                return false;
            }
        }

        /// <summary>
        /// Updates a game for a specific appid.
        /// Endpoint: /resellerupdate
        /// </summary>
        /// <param name="appId">The application ID</param>
        /// <returns>True if the update was successful, false otherwise</returns>
        public async Task<bool> UpdateGameAsync(string appId)
        {
            try
            {
                var url = BuildUrl("/resellerupdate", appId);
                _logger.Info($"Updating game for app {appId}...");
                _logger.Debug($"URL: {url}");
                
                var response = await _httpClient.GetAsync(url);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    _logger.Info($"Game updated successfully for app {appId}. Response: {content}");
                    return true;
                }
                else
                {
                    _logger.Error($"Failed to update game for app {appId}. Status: {response.StatusCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception while updating game for app {appId}", ex);
                return false;
            }
        }

        /// <summary>
        /// Fetches game information from the Steam API, including:
        /// - Name (traduzido)
        /// - Header image URL
        /// - List of DLC AppIDs
        /// </summary>
        /// <param name="appId">The Steam application ID</param>
        /// <returns>GameItem with complete data, or null if not found</returns>
        public async Task<GameItem?> GetSteamGameInfoAsync(string appId)
        {
            try
            {
                var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=brazilian";
                _logger.Info($"Fetching Steam info for app {appId}...");
                
                var response = await _httpClient.GetAsync(url);
                
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    
                    // Parse the JSON - the key is the appid
                    using var doc = JsonDocument.Parse(json);
                    
                    if (doc.RootElement.TryGetProperty(appId, out var appElement))
                    {
                        if (appElement.TryGetProperty("success", out var successElement) && successElement.GetBoolean())
                        {
                            if (appElement.TryGetProperty("data", out var dataElement))
                            {
                                var name = dataElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                                    ? nameElement.GetString() ?? $"App {appId}" 
                                    : $"App {appId}";
                                
                                // Extract header_image from API response
                                string? headerImage = null;
                                if (dataElement.TryGetProperty("header_image", out var headerImageElement) && headerImageElement.ValueKind == JsonValueKind.String)
                                {
                                    headerImage = headerImageElement.GetString();
                                }
                                
                                // Extract DLC list from API response
                                var dlcIds = new List<int>();
                                if (dataElement.TryGetProperty("dlc", out var dlcElement) && dlcElement.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var dlcItem in dlcElement.EnumerateArray())
                                    {
                                        if (dlcItem.ValueKind == JsonValueKind.Number && dlcItem.TryGetInt32(out var dlcId))
                                        {
                                            dlcIds.Add(dlcId);
                                        }
                                    }
                                }
                                
                                _logger.Info($"Steam info fetched for app {appId}: {name} (DLCs: {dlcIds.Count})");
                                
                                var shortDescription = dataElement.TryGetProperty("short_description", out var shortDescriptionElement) && shortDescriptionElement.ValueKind == JsonValueKind.String
                                    ? shortDescriptionElement.GetString()
                                    : null;

                                var releaseDate = TryReadNestedString(dataElement, "release_date", "date");

                                return new GameItem
                                {
                                    AppId = appId,
                                    Name = name,
                                    HeaderImageUrl = headerImage,
                                    DlcIds = dlcIds,
                                    ShortDescription = shortDescription,
                                    DevelopersText = ReadStringArray(dataElement, "developers"),
                                    PublishersText = ReadStringArray(dataElement, "publishers"),
                                    ReleaseDateText = releaseDate
                                };
                            }
                        }
                    }
                    
                    // Game not found or API returned failure
                    _logger.Warning($"Steam info not found for app {appId}");
                    return new GameItem { AppId = appId, Name = $"App {appId}" };
                }
                
                _logger.Warning($"Failed to fetch Steam info for app {appId}. Status: {response.StatusCode}");
                return new GameItem { AppId = appId, Name = $"App {appId}" };
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception while fetching Steam info for app {appId}", ex);
                return new GameItem { AppId = appId, Name = $"App {appId}" };
            }
        }

        /// <summary>
        /// Downloads the original ZIP package returned by the RyuManifest secure_download endpoint.
        /// This preserves file names, compression, and Lua content exactly as provided by the API.
        /// </summary>
        public async Task<byte[]?> DownloadManifestZipAsync(string appId)
        {
            try
            {
                var url = BuildUrl("/secure_download", appId);
                _logger.Info($"Downloading original manifest ZIP for app {appId}...");
                _logger.Debug($"URL: {url}");

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Error($"Failed to download manifest ZIP for app {appId}. Status: {response.StatusCode}");
                    return null;
                }

                var content = await response.Content.ReadAsByteArrayAsync();
                if (content.Length < 64 || content[0] != 0x50 || content[1] != 0x4B)
                {
                    _logger.Error($"Manifest ZIP for app {appId} is invalid or not a ZIP payload ({content.Length} bytes)");
                    return null;
                }

                _logger.Info($"Original manifest ZIP downloaded for app {appId} ({content.Length} bytes)");
                return content;
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception while downloading manifest ZIP for app {appId}", ex);
                return null;
            }
        }

        private static string? ReadStringArray(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var arrayElement) || arrayElement.ValueKind != JsonValueKind.Array)
                return null;

            var values = arrayElement
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            return values.Length == 0 ? null : string.Join(", ", values);
        }

        private static string? TryReadNestedString(JsonElement element, string objectName, string propertyName)
        {
            if (element.TryGetProperty(objectName, out var objectElement)
                && objectElement.ValueKind == JsonValueKind.Object
                && objectElement.TryGetProperty(propertyName, out var valueElement)
                && valueElement.ValueKind == JsonValueKind.String)
            {
                return valueElement.GetString();
            }

            return null;
        }

        private async Task<List<GameItem>> GetSteamAppListAsync()
        {
            if (_steamAppListCache != null && DateTime.UtcNow - _steamAppListCacheTime < TimeSpan.FromHours(12))
                return _steamAppListCache;

            try
            {
                _logger.Info("Loading official Steam app list...");
                var json = await _httpClient.GetStringAsync(SteamAppListUrl);
                using var doc = JsonDocument.Parse(json);

                var apps = new List<GameItem>();
                if (doc.RootElement.TryGetProperty("applist", out var appList)
                    && appList.TryGetProperty("apps", out var appsElement)
                    && appsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var app in appsElement.EnumerateArray())
                    {
                        if (!app.TryGetProperty("appid", out var appIdElement) || !appIdElement.TryGetInt32(out var appId))
                            continue;

                        if (!app.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
                            continue;

                        var name = nameElement.GetString();
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        apps.Add(new GameItem
                        {
                            AppId = appId.ToString(),
                            Name = name
                        });
                    }
                }

                _steamAppListCache = apps
                    .GroupBy(game => game.AppId)
                    .Select(group => group.First())
                    .OrderBy(game => game.Name)
                    .ToList();
                _steamAppListCacheTime = DateTime.UtcNow;

                _logger.Info($"Official Steam app list loaded: {_steamAppListCache.Count:N0} apps");
                return _steamAppListCache;
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to load official Steam app list", ex);
                return _steamAppListCache ?? new List<GameItem>();
            }
        }

        private async Task<GameItem> EnrichWithSteamDetailsAsync(GameItem game)
        {
            await _steamDetailsLimiter.WaitAsync();
            try
            {
                var details = await GetSteamGameInfoAsync(game.AppId);
                if (details == null)
                    return game;

                game.Name = string.IsNullOrWhiteSpace(details.Name) || details.Name.StartsWith("App ", StringComparison.OrdinalIgnoreCase)
                    ? game.Name
                    : details.Name;
                if (!string.IsNullOrWhiteSpace(details.HeaderImageUrl))
                    game.HeaderImageUrl = details.HeaderImageUrl;
                else if (string.IsNullOrWhiteSpace(game.HeaderImageUrl))
                    game.HeaderImageUrl = await FindRawgImageAsync(game.Name, game.AppId);

                game.DlcIds = details.DlcIds;
                game.ShortDescription = details.ShortDescription;
                game.DevelopersText = details.DevelopersText;
                game.PublishersText = details.PublishersText;
                game.ReleaseDateText = details.ReleaseDateText;

                return game;
            }
            finally
            {
                _steamDetailsLimiter.Release();
            }
        }

        private async Task<string?> FindRawgImageAsync(string gameName, string appId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(gameName))
                    return null;

                var encodedName = Uri.EscapeDataString(gameName);
                var url = $"https://api.rawg.io/api/games?search={encodedName}&key={RawgApiKey}&stores=1&page_size=5";
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("results", out var resultsArray) || resultsArray.ValueKind != JsonValueKind.Array)
                    return null;

                string? firstImage = null;
                foreach (var gameNode in resultsArray.EnumerateArray())
                {
                    var rawgGame = ParseRawgGameNode(gameNode);
                    if (string.IsNullOrWhiteSpace(rawgGame.HeaderImageUrl))
                        continue;

                    firstImage ??= rawgGame.HeaderImageUrl;
                    if (!string.IsNullOrWhiteSpace(appId) && rawgGame.AppId == appId)
                        return rawgGame.HeaderImageUrl;
                }

                return firstImage;
            }
            catch (Exception ex)
            {
                _logger.Warning($"RAWG image fallback failed for '{gameName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Checks if the API is accessible and the connection is working.
        /// </summary>
        /// <returns>True if the API is accessible, false otherwise</returns>
        public async Task<bool> CheckApiHealthAsync()
        {
            try
            {
                // Use a simple request to check connectivity
                var testAppId = "0"; // Use 0 as a test
                var url = BuildUrl("/resellerrequest", testAppId);
                
                var response = await _httpClient.GetAsync(url);
                
                // We're just checking if we can reach the server
                // Even if it returns an error for invalid appid, the connection is working
                _logger.Info($"API health check completed. Status: {response.StatusCode}");
                return response.IsSuccessStatusCode || (int)response.StatusCode == 400;
            }
            catch (Exception ex)
            {
                _logger.Error("API health check failed", ex);
                return false;
            }
        }

        /// <summary>
        /// Parses a RAWG game node to extract name, image URL, and Steam AppID.
        /// Uses ultra-safe null checks to prevent NullReferenceException.
        /// Tries: external_id → url → id fallback for Steam AppID extraction.
        /// </summary>
        private static GameItem ParseRawgGameNode(JsonElement gameNode)
        {
            // Safe name extraction
            string nome = gameNode.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == System.Text.Json.JsonValueKind.String
                ? nameProp.GetString() ?? "Unknown Game"
                : "Unknown Game";

            // Safe image URL extraction
            string? imagemUrl = null;
            if (gameNode.TryGetProperty("background_image", out var imgProp) && imgProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                imagemUrl = imgProp.GetString();
            }

            string steamAppId = "0";

            // Ultra-safe stores array scanning
            if (gameNode.TryGetProperty("stores", out var storesNode) && storesNode.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var storeRoot in storesNode.EnumerateArray())
                {
                    try
                    {
                        // Check if store object exists
                        if (storeRoot.TryGetProperty("store", out var storeObj) && storeObj.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            // RAWG store ID 1 = Steam
                            if (storeObj.TryGetProperty("id", out var storeIdProp) && storeIdProp.TryGetInt32(out var storeId) && storeId == 1)
                            {
                                // Method 1: Try external_id (most reliable when available)
                                if (storeRoot.TryGetProperty("external_id", out var extIdProp) && extIdProp.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    var extId = extIdProp.GetString();
                                    if (!string.IsNullOrEmpty(extId) && extId.All(char.IsDigit))
                                    {
                                        steamAppId = extId;
                                        break;
                                    }
                                }

                                // Method 2: Extract from URL (e.g. https://store.steampowered.com/app/12345/)
                                if (storeRoot.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    var storeUrl = urlProp.GetString();
                                    if (!string.IsNullOrEmpty(storeUrl))
                                    {
                                        var appIdMatch = Regex.Match(storeUrl, @"/app/(\d+)");
                                        if (appIdMatch.Success)
                                        {
                                            steamAppId = appIdMatch.Groups[1].Value;
                                            break;
                                        }
                                    }
                                }

                                // Method 3: Try the store root id field as fallback
                                if (storeRoot.TryGetProperty("id", out var rootIdProp) && rootIdProp.TryGetInt32(out var rootId) && rootId > 0)
                                {
                                    steamAppId = rootId.ToString();
                                    break;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Skip malformed store entries silently
                        continue;
                    }
                }
            }

            return new GameItem
            {
                AppId = steamAppId,
                Name = nome,
                HeaderImageUrl = imagemUrl
            };
        }

        /// <summary>
        /// Searches the official Steam app list first, then falls back to RAWG.
        /// </summary>
        public async Task<List<GameItem>> SearchGamesByNameAsync(string gameName)
        {
            var storeResults = await SearchSteamStoreAsync(gameName);
            if (storeResults.Count > 0)
                return storeResults;

            var rawgResults = await SearchRawgGamesByNameAsync(gameName);
            if (rawgResults.Count > 0)
                return rawgResults;

            return await SearchSteamGamesByNameAsync(gameName);
        }

        private async Task<List<GameItem>> SearchSteamStoreAsync(string gameName)
        {
            var results = new List<GameItem>();

            try
            {
                var encodedName = Uri.EscapeDataString(gameName.Trim());
                var url = $"{SteamStoreSearchUrl}?term={encodedName}&cc=br&l=brazilian";
                _logger.Info($"Searching Steam Store for: {gameName}");

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warning($"Steam Store search failed. Status: {response.StatusCode}");
                    return results;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return results;

                foreach (var item in items.EnumerateArray().Take(10))
                {
                    if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var appId))
                        continue;

                    var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()
                        : $"App {appId}";

                    var image = item.TryGetProperty("tiny_image", out var imageElement) && imageElement.ValueKind == JsonValueKind.String
                        ? imageElement.GetString()
                        : null;

                    results.Add(new GameItem
                    {
                        AppId = appId.ToString(),
                        Name = string.IsNullOrWhiteSpace(name) ? $"App {appId}" : name,
                        HeaderImageUrl = image
                    });
                }

                var enriched = await Task.WhenAll(results.Select(EnrichWithSteamDetailsAsync));
                results = enriched
                    .GroupBy(game => game.AppId)
                    .Select(group => group.First())
                    .ToList();

                _logger.Info($"Steam Store search returned {results.Count} results for '{gameName}'");
            }
            catch (Exception ex)
            {
                _logger.Error($"Steam Store search failed for '{gameName}'", ex);
            }

            return results;
        }

        private async Task<List<GameItem>> SearchSteamGamesByNameAsync(string gameName)
        {
            try
            {
                var apps = await GetSteamAppListAsync();
                var query = gameName.Trim();

                var matches = apps
                    .Where(game => game.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(game => game.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(game => game.Name.Length)
                    .Take(8)
                    .Select(game => new GameItem { AppId = game.AppId, Name = game.Name })
                    .ToList();

                var enriched = await Task.WhenAll(matches.Select(EnrichWithSteamDetailsAsync));
                var results = enriched.ToList();

                _logger.Info($"Official Steam search returned {results.Count} results for '{gameName}'");
                return results;
            }
            catch (Exception ex)
            {
                _logger.Error($"Official Steam search failed for '{gameName}'", ex);
                return new List<GameItem>();
            }
        }

        private async Task<List<GameItem>> SearchRawgGamesByNameAsync(string gameName)
        {
            var results = new List<GameItem>();

            try
            {
                var encodedName = Uri.EscapeDataString(gameName);
                var url = $"https://api.rawg.io/api/games?search={encodedName}&key={RawgApiKey}&stores=1&page_size=5";
                _logger.Info($"Searching RAWG for: {gameName}");

                var response = await _httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("results", out var resultsArray) && resultsArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var gameNode in resultsArray.EnumerateArray().Take(5))
                        {
                            try
                            {
                                var gameItem = ParseRawgGameNode(gameNode);
                                if (gameItem.AppId != "0")
                                    results.Add(gameItem);
                            }
                            catch (Exception ex)
                            {
                                _logger.Warning($"Error parsing RAWG search result item: {ex.Message}");
                            }
                        }

                        _logger.Info($"RAWG search returned {results.Count} results for '{gameName}'");
                    }
                }
                else
                {
                    _logger.Warning($"RAWG API request failed. Status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception while searching RAWG for '{gameName}'", ex);
            }

            return results;
        }

        /// <summary>
        /// Loads the catalog from the official Steam app list, then falls back to RAWG.
        /// </summary>
        public async Task<(int TotalCount, List<GameItem> Games, string? NextUrl, string? PreviousUrl)> LoadCatalogAsync(int page = 1, int pageSize = 20)
        {
            if (page == 1)
            {
                var trendingCatalog = await LoadSteamTrendingCatalogAsync(pageSize);
                if (trendingCatalog.Games.Count > 0)
                    return trendingCatalog;
            }

            var steamCatalog = await LoadSteamCatalogAsync(page, pageSize);
            if (steamCatalog.Games.Count > 0)
                return steamCatalog;

            _logger.Warning("Official Steam catalog returned no games. Falling back to RAWG.");
            return await LoadRawgCatalogAsync(page, pageSize);
        }

        private async Task<(int TotalCount, List<GameItem> Games, string? NextUrl, string? PreviousUrl)> LoadSteamTrendingCatalogAsync(int pageSize)
        {
            try
            {
                _logger.Info("Loading Steam trending games...");
                var json = await _httpClient.GetStringAsync(SteamFeaturedCategoriesUrl);
                using var doc = JsonDocument.Parse(json);

                var trending = new List<GameItem>();
                AddFeaturedCategoryItems(doc.RootElement, "top_sellers", trending);
                AddFeaturedCategoryItems(doc.RootElement, "new_releases", trending);
                AddFeaturedCategoryItems(doc.RootElement, "specials", trending);

                var unique = trending
                    .Where(game => !string.IsNullOrWhiteSpace(game.AppId) && game.AppId != "0")
                    .GroupBy(game => game.AppId)
                    .Select(group => group.First())
                    .Take(Math.Max(1, pageSize))
                    .ToList();

                if (unique.Count == 0)
                    return (0, new List<GameItem>(), null, null);

                var enriched = await Task.WhenAll(unique.Select(EnrichWithSteamDetailsAsync));
                var games = enriched.ToList();

                _logger.Info($"Steam trending catalog loaded: {games.Count} games");
                return (games.Count, games, null, null);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load Steam trending games: {ex.Message}");
                return (0, new List<GameItem>(), null, null);
            }
        }

        private static void AddFeaturedCategoryItems(JsonElement root, string categoryName, List<GameItem> target)
        {
            if (!root.TryGetProperty(categoryName, out var category)
                || !category.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var appId))
                    continue;

                var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : $"App {appId}";

                var image = item.TryGetProperty("header_image", out var imageElement) && imageElement.ValueKind == JsonValueKind.String
                    ? imageElement.GetString()
                    : null;

                target.Add(new GameItem
                {
                    AppId = appId.ToString(),
                    Name = string.IsNullOrWhiteSpace(name) ? $"App {appId}" : name,
                    HeaderImageUrl = image
                });
            }
        }

        private async Task<(int TotalCount, List<GameItem> Games, string? NextUrl, string? PreviousUrl)> LoadSteamCatalogAsync(int page = 1, int pageSize = 20)
        {
            try
            {
                var apps = await GetSteamAppListAsync();
                if (apps.Count == 0)
                    return (0, new List<GameItem>(), null, null);

                var safePage = Math.Max(1, page);
                var safePageSize = Math.Max(1, pageSize);
                var pageItems = apps
                    .Skip((safePage - 1) * safePageSize)
                    .Take(safePageSize)
                    .Select(game => new GameItem { AppId = game.AppId, Name = game.Name })
                    .ToList();

                var enriched = await Task.WhenAll(pageItems.Select(EnrichWithSteamDetailsAsync));
                var games = enriched.ToList();

                _logger.Info($"Official Steam catalog page {safePage} loaded: {games.Count} games");
                return (apps.Count, games, null, null);
            }
            catch (Exception ex)
            {
                _logger.Error("Exception while loading official Steam catalog", ex);
                return (0, new List<GameItem>(), null, null);
            }
        }

        private async Task<(int TotalCount, List<GameItem> Games, string? NextUrl, string? PreviousUrl)> LoadRawgCatalogAsync(int page = 1, int pageSize = 20)
        {
            var games = new List<GameItem>();
            int totalCount = 0;
            string? nextUrl = null;
            string? previousUrl = null;

            try
            {
                // Constrói a URL baseada no número da página solicitado pela UI
                var url = $"https://api.rawg.io/api/games?key={RawgApiKey}&page={page}&page_size={pageSize}&stores=1";
                _logger.Info($"Loading catalog: {url}");

                var response = await _httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    var root = doc.RootElement;
                    totalCount = root.TryGetProperty("count", out var countEl) ? countEl.GetInt32() : 0;

                    // Read next/previous URLs safely
                    if (root.TryGetProperty("next", out var nextProp) && nextProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        nextUrl = nextProp.GetString();
                    if (root.TryGetProperty("previous", out var prevProp) && prevProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        previousUrl = prevProp.GetString();

                    if (root.TryGetProperty("results", out var resultsArray) && resultsArray.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var gameNode in resultsArray.EnumerateArray())
                        {
                            try
                            {
                                var gameItem = ParseRawgGameNode(gameNode);

                                // FILTER: Only add games that have a valid Steam AppId
                                // Games with AppId "0" would crash the UI render (IconBitmap tries to load header.jpg for AppId 0)
                                if (gameItem.AppId != "0" && !string.IsNullOrEmpty(gameItem.AppId))
                                {
                                    games.Add(gameItem);
                                }
                                else
                                {
                                    _logger.Debug($"Skipping game '{gameItem.Name}' - no valid Steam AppId");
                                }
                            }
                            catch (Exception ex)
                            {
                                // Log individual item error but continue processing
                                _logger.Warning($"Error parsing catalog item: {ex.Message}");
                                continue;
                            }
                        }

                        _logger.Info($"Catalog loaded: {games.Count} valid games (total: {totalCount}, skipped: {resultsArray.GetArrayLength() - games.Count})");
                    }
                }
                else
                {
                    _logger.Warning($"Failed to load catalog. Status: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Exception while loading catalog", ex);
            }

            return (totalCount, games, nextUrl, previousUrl);
        }

        /// <summary>
        /// Downloads both manifest and Lua files for a game in parallel.
        /// </summary>
        /// <param name="appId">The application ID</param>
        /// <returns>A tuple containing (manifestContent, luaContent), or (null, null) if failed</returns>
        public async Task<(byte[]? Manifest, string? Lua)> DownloadGameFilesAsync(string appId)
        {
            try
            {
                _logger.Info($"Downloading game files for app {appId}...");
                
                // Download both files in parallel
                var manifestTask = DownloadManifestAsync(appId);
                var luaTask = DownloadLuaAsync(appId);
                
                await Task.WhenAll(manifestTask, luaTask);
                
                var manifest = await manifestTask;
                var lua = await luaTask;
                
                if (manifest != null && lua != null)
                {
                    _logger.Info($"All game files downloaded successfully for app {appId}");
                    return (manifest, lua);
                }
                else
                {
                    _logger.Error($"Failed to download all game files for app {appId}");
                    return (null, null);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Exception while downloading game files for app {appId}", ex);
                return (null, null);
            }
        }
    }
}
