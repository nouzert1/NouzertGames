using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Text.Json.Serialization;

namespace NouzertGames.Models
{
    /// <summary>
    /// Represents a game item with information from Steam API.
    /// Used for displaying game cards with cover images in the catalog.
    /// </summary>
    public class GameItem : INotifyPropertyChanged
    {
        private bool _actionsEnabled = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// The Steam application ID.
        /// </summary>
        public string AppId { get; set; } = string.Empty;

        /// <summary>
        /// The game name from Steam.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Short description from Steam appdetails, when available.
        /// </summary>
        [JsonIgnore]
        public string? ShortDescription { get; set; }

        /// <summary>
        /// Developers from Steam appdetails.
        /// </summary>
        [JsonIgnore]
        public string? DevelopersText { get; set; }

        /// <summary>
        /// Publishers from Steam appdetails.
        /// </summary>
        [JsonIgnore]
        public string? PublishersText { get; set; }

        /// <summary>
        /// Release date from Steam appdetails.
        /// </summary>
        [JsonIgnore]
        public string? ReleaseDateText { get; set; }

        /// <summary>
        /// URL direta do banner header vinda da Steam API.
        /// </summary>
        [JsonIgnore]
        public string? HeaderImageUrl { get; set; }

        /// <summary>
        /// Lista de AppIDs das DLCs do jogo (obtida da Steam API).
        /// </summary>
        [JsonIgnore]
        public List<int> DlcIds { get; set; } = new List<int>();

        [JsonIgnore]
        public bool ActionsEnabled
        {
            get => _actionsEnabled;
            set
            {
                if (_actionsEnabled == value)
                    return;

                _actionsEnabled = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// URL used directly by WPF Image.Source.
        /// </summary>
        [JsonIgnore]
        public string? ImageUrl => GetImageUrl();

        /// <summary>
        /// BitmapImage for WPF binding with proper caching.
        /// Uses HeaderImageUrl from API when available, falls back to constructed URL.
        /// </summary>
        private BitmapImage? _iconBitmap;

        [JsonIgnore]
        public BitmapImage? IconBitmap
        {
            get
            {
                if (_iconBitmap != null) return _iconBitmap;
                
                string? url = GetImageUrl();
                if (string.IsNullOrEmpty(url)) return null;

                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(url, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelHeight = 75; // Otimizado para o layout 160x75
                    bitmap.EndInit();
                    bitmap.Freeze(); // Crucial para performance em listas WPF
                    _iconBitmap = bitmap;
                    return _iconBitmap;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Erro ao carregar imagem para o AppId {AppId}: {ex.Message}");
                    return null;
                }
            }
        }

        private string? GetImageUrl()
        {
            if (!string.IsNullOrWhiteSpace(HeaderImageUrl))
                return HeaderImageUrl;

            if (!string.IsNullOrEmpty(AppId))
                return $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/header.jpg";

            return null;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Response model for Steam API appdetails endpoint.
    /// </summary>
    public class SteamAppDetailsResponse
    {
        [JsonPropertyName("{appid}")]
        public AppDetails? AppDetails { get; set; }
    }

    /// <summary>
    /// Contains the detailed information about a Steam app.
    /// </summary>
    public class AppDetails
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("data")]
        public SteamGameData? Data { get; set; }
    }

    /// <summary>
    /// The actual game data from Steam API.
    /// </summary>
    public class SteamGameData
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("required_age")]
        public int RequiredAge { get; set; }

        [JsonPropertyName("is_free")]
        public bool IsFree { get; set; }

        [JsonPropertyName("detailed_description")]
        public string? DetailedDescription { get; set; }

        [JsonPropertyName("short_description")]
        public string? ShortDescription { get; set; }

        [JsonPropertyName("header_image")]
        public string? HeaderImage { get; set; }

        [JsonPropertyName("website")]
        public string? Website { get; set; }

        [JsonPropertyName("developers")]
        public string[]? Developers { get; set; }

        [JsonPropertyName("publishers")]
        public string[]? Publishers { get; set; }

        [JsonPropertyName("platforms")]
        public SteamPlatforms? Platforms { get; set; }

        [JsonPropertyName("categories")]
        public SteamCategory[]? Categories { get; set; }

        [JsonPropertyName("release_date")]
        public SteamReleaseDate? ReleaseDate { get; set; }

        /// <summary>
        /// Lista de DLCs do jogo (cada uma contém um "id" e "name").
        /// </summary>
        [JsonPropertyName("dlc")]
        public int[]? Dlc { get; set; }
    }

    /// <summary>
    /// Platform support information.
    /// </summary>
    public class SteamPlatforms
    {
        [JsonPropertyName("windows")]
        public bool Windows { get; set; }

        [JsonPropertyName("mac")]
        public bool Mac { get; set; }

        [JsonPropertyName("linux")]
        public bool Linux { get; set; }
    }

    /// <summary>
    /// Category information.
    /// </summary>
    public class SteamCategory
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Release date information.
    /// </summary>
    public class SteamReleaseDate
    {
        [JsonPropertyName("coming_soon")]
        public bool ComingSoon { get; set; }

        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;
    }
}
