using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using NouzertGames.Models;
using NouzertGames.Services;

namespace NouzertGames.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly LoggerService _logger;
        private readonly SteamService _steamService;
        private readonly SteamToolsService _steamToolsService;
        private readonly RyuApiService _ryuApiService;
        private readonly ConfigService _configService;
        private readonly LocalProtectionService _localProtectionService;
        private GameConfig _config;
        private bool _accessGranted;
        private NouzertGamesKeyState _nouzertGamesKeyState = NouzertGamesKeyState.NotConfigured;

        private const string RequiredAccessKey = "Nouzert";
        private const string LocalActivationMarker = "NouzertGames.Activated";
        private const int DwmWindowAttributeUseImmersiveDarkMode = 20;

        // Pagination state
        private int _currentPage = 1;
        private int _totalCount = 0;
        private int _pageSize = 20;
        private bool _isSearchPlaceholderActive = true;
        private readonly System.Windows.Threading.DispatcherTimer _searchDebounceTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        private int _searchVersion;

        private bool CanUseProtectedActions => _accessGranted && _nouzertGamesKeyState == NouzertGamesKeyState.Active;

        private enum NouzertGamesKeyState
        {
            NotConfigured,
            Validating,
            Active,
            Expired
        }

        public MainWindow()
        {
            InitializeComponent();

            // Initialize services
            _logger = new LoggerService();
            _configService = new ConfigService();
            _localProtectionService = new LocalProtectionService();
            _steamService = new SteamService(_logger);
            _steamToolsService = new SteamToolsService(_logger);
            _ryuApiService = new RyuApiService(_logger);
            _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

            // Load configuration
            _config = _configService.LoadConfig();
            _accessGranted = HasValidLocalActivation();
            ImportRyuuManifestPathsIfAvailable();
            _ryuApiService.AuthCode = GetConfiguredRyuManifestKey();
            _nouzertGamesKeyState = string.IsNullOrWhiteSpace(_ryuApiService.AuthCode)
                ? NouzertGamesKeyState.NotConfigured
                : NouzertGamesKeyState.Validating;

            // Set up UI
            InitializeUI();
            UpdateActivationStatus();
            UpdateRyuManifestKeyStatus();
            
            // Start background tasks
            _ = InitializeAsync();
            _ = RefreshNouzertGamesKeyStatusAsync();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleWindowState();
                return;
            }

            DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleWindowState();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ToggleWindowState()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            EnableDarkTitleBar();
        }

        private void EnableDarkTitleBar()
        {
            if (Environment.OSVersion.Version.Major < 10)
                return;

            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var enabled = 1;
                _ = DwmSetWindowAttribute(
                    hwnd,
                    DwmWindowAttributeUseImmersiveDarkMode,
                    ref enabled,
                    Marshal.SizeOf<int>());
            }
            catch
            {
                // The custom app header still carries the visual identity if DWM dark mode is unavailable.
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private async System.Threading.Tasks.Task InitializeAsync()
        {
            AuthCodeTextBox.Clear();

            // Detect Steam
            await System.Threading.Tasks.Task.Delay(500); // Small delay for UI to render
            DetectSteam();

            // Detect and initialize SteamTools
            DetectSteamTools();

            // Update log display
            UpdateLogDisplay();

            // Load installed games
            LoadInstalledGames();

            // Load initial catalog from RAWG
            await LoadCatalogPageAsync();

            _logger.Info("Application initialized successfully");
        }

        private string GetConfiguredRyuManifestKey()
        {
            var environmentKey = Environment.GetEnvironmentVariable("RYUMANIFEST_AUTH_CODE")
                ?? Environment.GetEnvironmentVariable("RYU_MANIFEST_AUTH_CODE");

            if (!string.IsNullOrWhiteSpace(environmentKey))
                return environmentKey.Trim();

            if (_localProtectionService.TryUnprotect(_config.ProtectedAuthCode, out var protectedKey))
                return protectedKey.Trim();

            var legacyKey = _config.AuthCode?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(legacyKey))
            {
                SaveProtectedRyuManifestKey(legacyKey);
                return legacyKey;
            }

            return string.Empty;
        }

        private bool HasValidLocalActivation()
        {
            return _localProtectionService.TryUnprotect(_config.LocalActivationToken, out var marker)
                && string.Equals(marker, LocalActivationMarker, StringComparison.Ordinal);
        }

        private void SaveProtectedRyuManifestKey(string key)
        {
            _config.ProtectedAuthCode = _localProtectionService.Protect(key);
            _config.AuthCode = null;
            _configService.SaveConfig(_config);
        }

        private void InitializeUI()
        {
            // Set up tab selection
            SelectTab("Catalog");

            // Apply saved window preferences
            if (_config.Preferences != null)
            {
                Width = _config.Preferences.WindowWidth;
                Height = _config.Preferences.WindowHeight;
                WindowState = _config.Preferences.IsMaximized ? WindowState.Maximized : WindowState.Normal;
            }
        }

        private void DetectSteam()
        {
            var steamPath = _steamService.GetSteamPath();
            if (!string.IsNullOrEmpty(steamPath))
            {
                SteamPathText.Text = steamPath;
                SteamPathText.Foreground = (Brush?)(new BrushConverter().ConvertFromString("#107c10") ?? Brushes.Green);

                var isRunning = _steamService.IsSteamRunning();
                SteamStatusText.Text = isRunning ? "Rodando" : "Nao funcionando";
                SteamStatusText.Foreground = isRunning 
                    ? (Brush?)(new BrushConverter().ConvertFromString("#107c10") ?? Brushes.Green)
                    : (Brush?)(new BrushConverter().ConvertFromString("#ff8c00") ?? Brushes.Orange);

                // Update injection paths display
                var steamExe = _steamService.GetSteamExecutablePath();
                if (string.IsNullOrWhiteSpace(_config.SteamExePath) && !string.IsNullOrWhiteSpace(steamExe))
                    _config.SteamExePath = steamExe;

                var stPlugInPath = _steamService.GetStPlugInDirectory();
                if (string.IsNullOrWhiteSpace(_config.SteamConfigPath) && !string.IsNullOrWhiteSpace(stPlugInPath))
                    _config.SteamConfigPath = stPlugInPath;

                var depotCachePath = _steamService.GetDepotCacheDirectory();
                if (string.IsNullOrWhiteSpace(_config.DepotCachePath) && !string.IsNullOrWhiteSpace(depotCachePath))
                    _config.DepotCachePath = depotCachePath;

                _configService.SaveConfig(_config);
                UpdateInjectionPathDisplay();
            }
            else
            {
                SteamPathText.Text = "Steam not detected";
                SteamPathText.Foreground = (Brush?)(new BrushConverter().ConvertFromString("#e81123") ?? Brushes.Red);
                SteamStatusText.Text = "Unknown";
                SteamStatusText.Foreground = (Brush?)(new BrushConverter().ConvertFromString("#e81123") ?? Brushes.Red);
                SteamExePathText.Text = "Steam not detected";
                LuaPathText.Text = "Steam not detected";
                ManifestPathText.Text = "Steam not detected";
            }
        }

        private void UpdateLogDisplay()
        {
            LogTextBox.Text = _logger.GetLogContent();
            LogTextBox.ScrollToEnd();
        }

        private void LoadInstalledGames()
        {
            InstalledGamesList.Items.Clear();

            var games = new List<InstalledGame>();
            var hiddenAppIds = new HashSet<string>(
                _config.HiddenLibraryAppIds ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            if (_config.InstalledGames != null)
                games.AddRange(_config.InstalledGames.Where(game => !hiddenAppIds.Contains(game.AppId)));

            var steamGames = _steamService.GetInstalledSteamGames();
            foreach (var steamGame in steamGames)
            {
                if (hiddenAppIds.Contains(steamGame.AppId))
                    continue;

                var existing = games.FirstOrDefault(game => game.AppId == steamGame.AppId);
                if (existing == null)
                {
                    games.Add(steamGame);
                }
                else
                {
                    existing.ManifestPath ??= steamGame.ManifestPath;
                    existing.BuildId ??= steamGame.BuildId;
                    existing.SizeOnDisk = existing.SizeOnDisk > 0 ? existing.SizeOnDisk : steamGame.SizeOnDisk;
                    existing.Name = string.IsNullOrWhiteSpace(existing.Name) ? steamGame.Name : existing.Name;
                }
            }

            games = games
                .OrderBy(game => game.Name ?? game.AppId)
                .ToList();

            if (games.Count == 0)
            {
                NoGamesText.Visibility = Visibility.Visible;
                return;
            }

            NoGamesText.Visibility = Visibility.Collapsed;

            foreach (var game in games)
            {
                var gameCard = CreateInstalledGameCard(game);
                InstalledGamesList.Items.Add(gameCard);
            }

            _logger.Info($"Library loaded with {games.Count} games ({steamGames.Count} from Steam manifests)");
        }

        private Border CreateInstalledGameCard(InstalledGame game)
        {
            var border = new Border
            {
                Style = (Style)FindResource("GameCardStyle"),
                MinHeight = 132,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Game name
            var nameText = new TextBlock
            {
                Text = !string.IsNullOrEmpty(game.Name) ? game.Name : $"App {game.AppId}",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimary"),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(nameText, 0);

            // App ID
            var appIdText = new TextBlock
            {
                Text = BuildInstalledGameDetails(game),
                FontSize = 12,
                Foreground = (Brush)FindResource("TextSecondary"),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(appIdText, 1);

            // Status
            var statusPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var statusDot = new TextBlock
            {
                Text = "Status:",
                Foreground = game.IsEnabled 
                    ? (Brush)FindResource("SuccessColor") 
                    : (Brush)FindResource("TextMuted"),
                FontSize = 12
            };
            var statusText = new TextBlock
            {
                Text = game.IsEnabled ? " Ativado" : " Desativado",
                Foreground = (Brush)FindResource("TextSecondary"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            statusPanel.Children.Add(statusDot);
            statusPanel.Children.Add(statusText);
            Grid.SetRow(statusPanel, 2);

            // Buttons
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            
            var removeButton = new Button
            {
                Content = "Remover",
                Style = (Style)FindResource("SecondaryButtonStyle"),
                Tag = game.AppId,
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6)
            };
            removeButton.Click += RemoveGameButton_Click;
            buttonPanel.Children.Add(removeButton);

            var updateButton = new Button
            {
                Content = "Atualizar",
                Style = (Style)FindResource("PrimaryButtonStyle"),
                Tag = game.AppId,
                FontSize = 12,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = game.IsEnabled
            };
            updateButton.Click += UpdateGameButton_Click;
            buttonPanel.Children.Add(updateButton);

            Grid.SetRow(buttonPanel, 3);
            buttonPanel.HorizontalAlignment = HorizontalAlignment.Right;

            grid.Children.Add(nameText);
            grid.Children.Add(appIdText);
            grid.Children.Add(statusPanel);
            grid.Children.Add(buttonPanel);

            border.Child = grid;
            return border;
        }

        private static string BuildInstalledGameDetails(InstalledGame game)
        {
            var details = new List<string> { $"ID: {game.AppId}" };

            if (!string.IsNullOrWhiteSpace(game.BuildId))
                details.Add($"Build: {game.BuildId}");

            if (game.SizeOnDisk > 0)
                details.Add($"Size: {FormatBytes(game.SizeOnDisk)}");

            if (!string.IsNullOrWhiteSpace(game.ManifestPath))
                details.Add("Steam manifest");

            return string.Join("  |  ", details);
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            var unit = 0;

            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return $"{size:0.##} {units[unit]}";
        }

        #region Event Handlers

        private void TabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string tabName)
            {
                SelectTab(tabName);
            }
        }

        private void SelectTab(string tabName)
        {
            // Hide all views
            CatalogView.Visibility = Visibility.Collapsed;
            GeneratorView.Visibility = Visibility.Collapsed;
            LibraryView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;

            // Reset tab styles
            CatalogTab.Foreground = (Brush)FindResource("TextSecondary");
            GeneratorTab.Foreground = (Brush)FindResource("TextSecondary");
            LibraryTab.Foreground = (Brush)FindResource("TextSecondary");
            SettingsTab.Foreground = (Brush)FindResource("TextSecondary");

            // Show selected view
            switch (tabName)
            {
                case "Catalog":
                    CatalogView.Visibility = Visibility.Visible;
                    CatalogTab.Foreground = (Brush)FindResource("TextPrimary");
                    break;
                case "Generator":
                    GeneratorView.Visibility = Visibility.Visible;
                    GeneratorTab.Foreground = (Brush)FindResource("TextPrimary");
                    break;
                case "Library":
                    LibraryView.Visibility = Visibility.Visible;
                    LibraryTab.Foreground = (Brush)FindResource("TextPrimary");
                    LoadInstalledGames();
                    break;
                case "Settings":
                    SettingsView.Visibility = Visibility.Visible;
                    SettingsTab.Foreground = (Brush)FindResource("TextPrimary");
                    UpdateLogDisplay();
                    DetectSteam();
                    break;
            }
        }

        private void SearchTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (_isSearchPlaceholderActive || SearchTextBox.Text == "Digite o AppID ou nome do jogo...")
            {
                SearchTextBox.Text = string.Empty;
                _isSearchPlaceholderActive = false;
            }
        }

        private void ImportRyuuManifestPathsIfAvailable()
        {
            var candidatePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Ryumanifest", "config.json"),
                Path.Combine(AppContext.BaseDirectory, "..", "Ryumanifest", "config.json"),
                Path.Combine(Environment.CurrentDirectory, "Ryumanifest", "config.json"),
                Path.Combine(Environment.CurrentDirectory, "..", "Ryumanifest", "config.json")
            };

            var referenceConfigPath = candidatePaths.FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(referenceConfigPath))
                return;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(referenceConfigPath));
                var root = doc.RootElement;
                var changed = false;

                changed |= SetConfigValueIfMissing(value => _config.SteamConfigPath = value, _config.SteamConfigPath, ReadJsonString(root, "steam_config_path"));
                changed |= SetConfigValueIfMissing(value => _config.DepotCachePath = value, _config.DepotCachePath, ReadJsonString(root, "depot_cache_path"));
                changed |= SetConfigValueIfMissing(value => _config.SteamExePath = value, _config.SteamExePath, ReadJsonString(root, "steam_exe_path"));
                changed |= SetConfigValueIfMissing(value => _config.SteamToolsPath = value, _config.SteamToolsPath, ReadJsonString(root, "steamtools_path"));
                if (root.TryGetProperty("mirror_manifests_to_steam_depotcache", out var mirrorProperty)
                    && mirrorProperty.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    _config.MirrorManifestsToSteamDepotcache = mirrorProperty.GetBoolean();
                    changed = true;
                }

                if (changed)
                {
                    _configService.SaveConfig(_config);
                    _logger.Info($"Imported RyuuManifest reference paths from: {referenceConfigPath}");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to import RyuuManifest reference config: {ex.Message}");
            }
        }

        private static string? ReadJsonString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }

        private static bool SetConfigValueIfMissing(Action<string> setter, string? currentValue, string? newValue)
        {
            if (!string.IsNullOrWhiteSpace(currentValue) || string.IsNullOrWhiteSpace(newValue))
                return false;

            setter(newValue);
            return true;
        }

        private void UpdateInjectionPathDisplay()
        {
            SteamExePathText.Text = !string.IsNullOrWhiteSpace(_config.SteamExePath) ? _config.SteamExePath : "Not found";
            LuaPathText.Text = !string.IsNullOrWhiteSpace(_config.SteamConfigPath) ? _config.SteamConfigPath : "Not found";
            ManifestPathText.Text = !string.IsNullOrWhiteSpace(_config.DepotCachePath) ? _config.DepotCachePath : "Not found";
        }

        private void SearchTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                _isSearchPlaceholderActive = true;
                SearchTextBox.Text = "Digite o AppID ou nome do jogo...";
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            _ = SearchForGameAsync();
        }

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _ = SearchForGameAsync();
            }
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSearchPlaceholderActive)
                return;

            if (!IsLoaded)
                return;

            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private async void SearchDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _searchDebounceTimer.Stop();

            var input = SearchTextBox.Text.Trim();
            if (input.Length < 2)
            {
                _currentPage = 1;
                await LoadCatalogPageAsync();
                return;
            }

            await SearchForGameAsync();
        }

        private async System.Threading.Tasks.Task SearchForGameAsync()
        {
            var input = SearchTextBox.Text.Trim();
            if (string.IsNullOrEmpty(input) || _isSearchPlaceholderActive)
            {
                SetStatusMessage("Digite um AppID ou nome de jogo", "Warning");
                return;
            }

            var searchVersion = ++_searchVersion;
            SetStatusMessage($"Buscando '{input}'...", "Info");

            // Clear existing cards
            GamesGrid.Items.Clear();

            // Detect if input is numeric (AppID) or text (game name)
            if (int.TryParse(input, out _))
            {
                // Numero: busca por AppID na Steam API
                await SearchByAppIdAsync(input);
            }
            else
            {
                // Texto: busca por nome na Steam Store e usa RAWG como fallback
                await SearchByNameAsync(input);
            }
        }

        private void GeneratorSearchTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (GeneratorSearchTextBox.Text == "Digite o AppID ou nome do jogo...")
                GeneratorSearchTextBox.Text = string.Empty;
        }

        private void GeneratorSearchTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(GeneratorSearchTextBox.Text))
                GeneratorSearchTextBox.Text = "Digite o AppID ou nome do jogo...";
        }

        private async void GeneratorSearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                await SearchGeneratorManifestsAsync();
        }

        private async void GeneratorSearchButton_Click(object sender, RoutedEventArgs e)
        {
            await SearchGeneratorManifestsAsync();
        }

        private async System.Threading.Tasks.Task SearchGeneratorManifestsAsync()
        {
            var input = GeneratorSearchTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input) || input == "Digite o AppID ou nome do jogo...")
            {
                SetStatusMessage("Digite um AppID ou nome de jogo", "Warning");
                return;
            }

            GeneratorResultsList.Items.Clear();
            GeneratorInfoText.Text = $"Pesquisando '{input}'...";
            SetStatusMessage($"Pesquisando manifests para '{input}'...", "Info");

            try
            {
                List<GameItem> results;
                if (int.TryParse(input, out _))
                {
                    var game = await _ryuApiService.GetSteamGameInfoAsync(input);
                    results = game != null ? new List<GameItem> { game } : new List<GameItem>();
                }
                else
                {
                    results = await _ryuApiService.SearchGamesByNameAsync(input);
                }

                foreach (var game in results)
                {
                    ApplyActionState(game);
                    GeneratorResultsList.Items.Add(game);
                }

                GeneratorInfoText.Text = results.Count > 0
                    ? $"{results.Count} resultado(s). Clique em Download ZIP para baixar .manifest + .lua."
                    : $"Nenhum resultado para '{input}'.";
                SetStatusMessage(results.Count > 0 ? "Resultados carregados" : "Nenhum resultado encontrado", results.Count > 0 ? "Success" : "Warning");
            }
            catch (Exception ex)
            {
                _logger.Error($"Erro ao pesquisar manifests para '{input}'", ex);
                GeneratorInfoText.Text = $"Erro ao pesquisar: {ex.Message}";
                SetStatusMessage($"Erro: {ex.Message}", "Error");
            }
        }

        private async void DownloadManifestZipButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string appId)
                return;

            if (!CanUseProtectedActions)
            {
                SetStatusMessage(GetBlockedNouzertGamesMessage(), "Error");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Salvar pacote manifest",
                FileName = $"{appId}_NouzertManifest.zip",
                Filter = "Arquivo ZIP (*.zip)|*.zip",
                DefaultExt = ".zip",
                AddExtension = true,
                OverwritePrompt = true
            };

            if (dialog.ShowDialog() != true)
                return;

            button.IsEnabled = false;
            button.Content = "Baixando...";
            SetStatusMessage($"Baixando pacote de {appId}...", "Info");

            try
            {
                var zipBytes = await _ryuApiService.DownloadManifestZipAsync(appId);
                if (zipBytes == null)
                {
                    if (await MarkNouzertGamesKeyExpiredIfInvalidAsync())
                    {
                        SetStatusMessage("Chave NouzertGames expirada. Adquira nova chave.", "Error");
                        return;
                    }

                    SetStatusMessage($"ZIP indisponivel para {appId}", "Error");
                    return;
                }

                File.WriteAllBytes(dialog.FileName, zipBytes);
                GeneratorInfoText.Text = $"ZIP salvo: {dialog.FileName}";
                SetStatusMessage($"ZIP baixado com sucesso: {appId}", "Success");
            }
            catch (Exception ex)
            {
                _logger.Error($"Erro ao baixar ZIP de manifest para {appId}", ex);
                SetStatusMessage($"Erro: {ex.Message}", "Error");
            }
            finally
            {
                button.IsEnabled = CanUseProtectedActions;
                button.Content = "Download ZIP";
            }
        }

        /// <summary>
        /// Searches for a game by its numeric Steam AppID.
        /// </summary>
        private async System.Threading.Tasks.Task SearchByAppIdAsync(string appId)
        {
            var activeSearch = _searchVersion;
            SetStatusMessage($"Buscando AppID: {appId}...", "Info");

            try
            {
                var gameInfo = await _ryuApiService.GetSteamGameInfoAsync(appId);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (activeSearch != _searchVersion)
                        return;

                    if (gameInfo != null)
                    {
                        GamesGrid.Items.Add(gameInfo);
                        ApplyActionState(gameInfo);
                        SetStatusMessage($"Encontrado: {gameInfo.Name}", "Success");
                    }
                    else
                    {
                        var fallback = new GameItem { AppId = appId, Name = $"App {appId}" };
                        ApplyActionState(fallback);
                        GamesGrid.Items.Add(fallback);
                        SetStatusMessage($"AppID: {appId} (nome nao encontrado)", "Warning");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Erro ao buscar informacoes do AppID {appId}", ex);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var fallback = new GameItem { AppId = appId, Name = $"App {appId}" };
                    ApplyActionState(fallback);
                    GamesGrid.Items.Add(fallback);
                    SetStatusMessage($"Erro: {ex.Message}", "Error");
                });
            }
        }

        /// <summary>
        /// Searches for games by name using Steam Store first and RAWG as fallback.
        /// </summary>
        private async System.Threading.Tasks.Task SearchByNameAsync(string gameName)
        {
            var activeSearch = _searchVersion;
            SetStatusMessage($"Buscando '{gameName}'...", "Info");

            try
            {
                var results = await _ryuApiService.SearchGamesByNameAsync(gameName);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (activeSearch != _searchVersion)
                        return;

                    if (results.Count > 0)
                    {
                        foreach (var game in results)
                        {
                            ApplyActionState(game);
                            GamesGrid.Items.Add(game);
                        }
                        SetStatusMessage($"{results.Count} resultado(s) para '{gameName}'", "Success");
                    }
                    else
                    {
                        SetStatusMessage($"Nenhum resultado para '{gameName}'", "Warning");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Erro ao buscar jogo por nome '{gameName}'", ex);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    SetStatusMessage($"Erro: {ex.Message}", "Error");
                });
            }
        }

        private async void AddGameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string appId)
            {
                button.IsEnabled = false;
                var originalContent = button.Content;
                button.Content = "Adicionando...";
                SetStatusMessage($"Baixando ZIP de {appId}...", "Info");

                try
                {
                    if (!appId.All(char.IsDigit))
                    {
                        SetStatusMessage("AppID invalido. Use apenas o ID numerico.", "Error");
                        return;
                    }

                    if (!CanUseProtectedActions)
                    {
                        SetStatusMessage(GetBlockedNouzertGamesMessage(), "Error");
                        return;
                    }

                    var zipBytes = await _ryuApiService.DownloadManifestZipAsync(appId);
                    if (zipBytes == null)
                    {
                        if (await MarkNouzertGamesKeyExpiredIfInvalidAsync())
                        {
                            SetStatusMessage("Chave NouzertGames expirada. Adquira nova chave.", "Error");
                            return;
                        }

                        SetStatusMessage($"ZIP indisponivel para {appId}", "Error");
                        return;
                    }

                    var (luaCount, manifestCount) = InstallManifestPackageDirectly(zipBytes);
                    _configService.AddInstalledGame(appId, (button.DataContext as GameItem)?.Name);
                    _config = _configService.LoadConfig();
                    _logger.Info($"Game {appId} added directly: {luaCount} Lua file(s), {manifestCount} manifest file(s)");
                    SetStatusMessage($"Jogo adicionado: {appId}", "Success");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Erro ao adicionar {appId}", ex);
                    SetStatusMessage($"Erro: {ex.Message}", "Error");
                }
                finally
                {
                    button.IsEnabled = CanUseProtectedActions;
                    button.Content = originalContent;
                }
            }
        }

        private (int LuaCount, int ManifestCount) InstallManifestPackageDirectly(byte[] zipBytes)
        {
            var luaDirectory = _config.SteamConfigPath;
            var manifestDirectory = _config.DepotCachePath;
            var steamRootDepotCache = GetSteamRootDepotCacheDirectory();

            if (string.IsNullOrWhiteSpace(luaDirectory))
                throw new InvalidOperationException("Caminho .lua (config/stplug-in) nao encontrado.");

            if (string.IsNullOrWhiteSpace(manifestDirectory))
                throw new InvalidOperationException("Caminho .manifest (config/depotcache) nao encontrado.");

            Directory.CreateDirectory(luaDirectory);
            Directory.CreateDirectory(manifestDirectory);

            using var zipStream = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);

            var luaCount = 0;
            var manifestCount = 0;

            foreach (var entry in archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)))
            {
                var extension = Path.GetExtension(entry.Name);
                string? targetDirectory = null;

                if (string.Equals(extension, ".lua", StringComparison.OrdinalIgnoreCase))
                {
                    targetDirectory = luaDirectory;
                    luaCount++;
                }
                else if (string.Equals(extension, ".manifest", StringComparison.OrdinalIgnoreCase))
                {
                    targetDirectory = manifestDirectory;
                    manifestCount++;
                }

                if (targetDirectory == null)
                    continue;

                var targetPath = Path.Combine(targetDirectory, entry.Name);
                entry.ExtractToFile(targetPath, overwrite: true);
                _logger.Info($"Extracted {entry.Name} to {targetDirectory}");

                if (_config.MirrorManifestsToSteamDepotcache
                    && string.Equals(extension, ".manifest", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(steamRootDepotCache)
                    && !string.Equals(Path.GetFullPath(targetDirectory), Path.GetFullPath(steamRootDepotCache), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(steamRootDepotCache);
                    var mirrorPath = Path.Combine(steamRootDepotCache, entry.Name);
                    File.Copy(targetPath, mirrorPath, overwrite: true);
                    _logger.Info($"Mirrored {entry.Name} to {steamRootDepotCache}");
                }
            }

            if (luaCount == 0 || manifestCount == 0)
                throw new InvalidOperationException($"Pacote invalido: {luaCount} arquivo(s) .lua e {manifestCount} arquivo(s) .manifest encontrados.");

            return (luaCount, manifestCount);
        }

        private string? GetSteamRootDepotCacheDirectory()
        {
            var steamExePath = _config.SteamExePath;
            var steamRoot = !string.IsNullOrWhiteSpace(steamExePath)
                ? Path.GetDirectoryName(steamExePath)
                : _steamService.GetSteamPath();

            return string.IsNullOrWhiteSpace(steamRoot)
                ? null
                : Path.Combine(steamRoot, "depotcache");
        }

        private void OpenFolder(string folderPath)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = folderPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.Warning($"Nao foi possivel abrir a pasta '{folderPath}': {ex.Message}");
            }
        }

        private void RemoveGameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string appId)
            {
                var result = MessageBox.Show(
                    $"Deseja remover {appId} da biblioteca e apagar os arquivos instalados por este app?",
                    "Confirmar remocao",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    var removedFiles = RemoveInstalledGameFiles(appId);
                    _configService.RemoveInstalledGame(appId);
                    _config = _configService.LoadConfig();
                    LoadInstalledGames();
                    SetStatusMessage($"Removido {appId} da biblioteca ({removedFiles} arquivo(s) apagado(s))", "Info");
                }
            }
        }

        private int RemoveInstalledGameFiles(string appId)
        {
            var removedFiles = 0;
            var candidatePaths = new List<string>();

            AddCandidateFile(candidatePaths, _config.SteamConfigPath, $"{appId}.lua");
            AddCandidateFile(candidatePaths, _config.DepotCachePath, $"{appId}.manifest");
            AddCandidateFile(candidatePaths, GetSteamRootDepotCacheDirectory(), $"{appId}.manifest");

            foreach (var filePath in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(filePath))
                        continue;

                    File.Delete(filePath);
                    removedFiles++;
                    _logger.Info($"Removed installed game file: {filePath}");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Nao foi possivel remover '{filePath}': {ex.Message}");
                }
            }

            return removedFiles;
        }

        private static void AddCandidateFile(List<string> paths, string? directory, string fileName)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return;

            paths.Add(Path.Combine(directory, fileName));
        }

        private async void UpdateGameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string appId)
            {
                button.IsEnabled = false;
                button.Content = "Atualizando...";
                SetStatusMessage($"Atualizando {appId}...", "Info");

                try
                {
                    if (!_accessGranted)
                    {
                        SetStatusMessage("Acesso bloqueado. Digite a chave de acesso em Configuracoes.", "Error");
                        button.IsEnabled = true;
                        button.Content = "Atualizar";
                        return;
                    }

                    var success = await _ryuApiService.UpdateGameAsync(appId);
                    
                    if (success)
                    {
                        SetStatusMessage($"Atualizado com sucesso: {appId}", "Success");
                        
                        // Update last update date
                        if (_config.InstalledGames != null)
                        {
                            var game = _config.InstalledGames.FirstOrDefault(g => g.AppId == appId);
                            if (game != null)
                            {
                                game.LastUpdateDate = DateTime.UtcNow;
                                _configService.SaveConfig(_config);
                            }
                        }
                    }
                    else
                    {
                        SetStatusMessage($"Falha ao atualizar {appId}", "Error");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error Atualizando {appId}", ex);
                    SetStatusMessage($"Error: {ex.Message}", "Error");
                }
                finally
                {
                    button.IsEnabled = true;
                    button.Content = "Atualizar";
                }
            }
        }

        private async void RestartSteamButton_Click(object sender, RoutedEventArgs e)
        {
            SetStatusMessage("Reiniciando Steam...", "Info");
            
            var success = await _steamService.RestartSteamAsync();
            
            if (success)
            {
                SetStatusMessage("Steam reiniciada com sucesso", "Success");
            }
            else
            {
                SetStatusMessage("Falha ao reiniciar a Steam", "Error");
            }
        }

        private void SaveAuthCodeButton_Click(object sender, RoutedEventArgs e)
        {
            var accessKey = AuthCodeTextBox.Text.Trim();
            AuthCodeTextBox.Clear();

            if (string.IsNullOrEmpty(accessKey))
            {
                SetStatusMessage("Digite a chave de acesso", "Warning");
                return;
            }

            if (!string.Equals(accessKey, RequiredAccessKey, StringComparison.Ordinal))
            {
                _accessGranted = false;
                UpdateActivationStatus();
                UpdateActionStates();
                SetStatusMessage("Chave de acesso invalida", "Error");
                return;
            }

            SaveLocalActivation();
            UpdateActivationStatus();
            UpdateActionStates();
            SetStatusMessage("Computador ativado com sucesso", "Success");
        }

        private void SaveLocalActivation()
        {
            _accessGranted = true;
            _config.LocalActivationToken = _localProtectionService.Protect(LocalActivationMarker);
            _configService.SaveConfig(_config);
        }

        private void DownloadSteamToolsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://steamtools.net/",
                    UseShellExecute = true
                });
                SetStatusMessage("Abrindo site oficial do SteamTools...", "Info");
            }
            catch (Exception ex)
            {
                _logger.Error("Falha ao abrir o site do SteamTools", ex);
                SetStatusMessage("Nao foi possivel abrir o site do SteamTools", "Error");
            }
        }

        private async void SaveRyuManifestKeyButton_Click(object sender, RoutedEventArgs e)
        {
            var resellerKey = RyuManifestKeyBox.Password.Trim();
            RyuManifestKeyBox.Clear();

            if (string.IsNullOrWhiteSpace(resellerKey))
            {
                SetStatusMessage("Informe a chave NouzertGames", "Warning");
                return;
            }

            SetStatusMessage("Validando chave NouzertGames...", "Info");
            var isValidKey = await _ryuApiService.ValidateAuthCodeAsync(resellerKey);
            if (!isValidKey)
            {
                _nouzertGamesKeyState = NouzertGamesKeyState.Expired;
                UpdateRyuManifestKeyStatus();
                UpdateActionStates();
                SetStatusMessage("Chave NouzertGames expirada. Adquira nova chave.", "Error");
                return;
            }

            SaveProtectedRyuManifestKey(resellerKey);
            _ryuApiService.AuthCode = resellerKey;
            _nouzertGamesKeyState = NouzertGamesKeyState.Active;
            UpdateRyuManifestKeyStatus();
            UpdateActionStates();
            SetStatusMessage("Chave NouzertGames salva localmente", "Success");
        }

        private void UpdateActivationStatus()
        {
            if (ActivationStatusText == null)
                return;

            ActivationStatusText.Text = _accessGranted
                ? "Status: este computador esta ativado."
                : "Status: computador ainda nao ativado. As acoes do catalogo permanecem indisponiveis.";
            ActivationStatusText.Foreground = _accessGranted
                ? (Brush)FindResource("SuccessColor")
                : (Brush)FindResource("WarningColor");
        }

        private void UpdateRyuManifestKeyStatus()
        {
            var configured = !string.IsNullOrWhiteSpace(_ryuApiService.AuthCode);
            var active = _nouzertGamesKeyState == NouzertGamesKeyState.Active;

            RyuManifestKeyStatusText.Text = _nouzertGamesKeyState switch
            {
                NouzertGamesKeyState.Active => "Status: chave NouzertGames ativa.",
                NouzertGamesKeyState.Validating => "Status: validando chave NouzertGames...",
                NouzertGamesKeyState.Expired => "Status: chave NouzertGames expirada. Adquira nova chave.",
                _ => "Status: insira a chave NouzertGames."
            };
            RyuManifestKeyStatusText.Foreground = _nouzertGamesKeyState switch
            {
                NouzertGamesKeyState.Active => (Brush)FindResource("SuccessColor"),
                NouzertGamesKeyState.Validating => (Brush)FindResource("WarningColor"),
                _ => (Brush)FindResource("ErrorColor")
            };

            if (RyuManifestKeyButton != null)
            {
                RyuManifestKeyButton.Content = configured && active
                    ? "Salvar chave local"
                    : "Insira a chave NouzertGames";
                RyuManifestKeyButton.Background = active
                    ? (Brush)FindResource("AccentColor")
                    : (Brush)FindResource("ErrorColor");
                RyuManifestKeyButton.Foreground = active
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#071019"))
                    : (Brush)FindResource("TextPrimary");
            }
        }

        private async System.Threading.Tasks.Task RefreshNouzertGamesKeyStatusAsync()
        {
            if (string.IsNullOrWhiteSpace(_ryuApiService.AuthCode))
            {
                _nouzertGamesKeyState = NouzertGamesKeyState.NotConfigured;
                UpdateRyuManifestKeyStatus();
                UpdateActionStates();
                return;
            }

            _nouzertGamesKeyState = NouzertGamesKeyState.Validating;
            UpdateRyuManifestKeyStatus();
            UpdateActionStates();

            var isValid = await _ryuApiService.ValidateCurrentAuthCodeAsync();
            _nouzertGamesKeyState = isValid
                ? NouzertGamesKeyState.Active
                : NouzertGamesKeyState.Expired;

            UpdateRyuManifestKeyStatus();
            UpdateActionStates();
        }

        private async System.Threading.Tasks.Task<bool> MarkNouzertGamesKeyExpiredIfInvalidAsync()
        {
            if (string.IsNullOrWhiteSpace(_ryuApiService.AuthCode))
            {
                _nouzertGamesKeyState = NouzertGamesKeyState.NotConfigured;
                UpdateRyuManifestKeyStatus();
                UpdateActionStates();
                return false;
            }

            var isValid = await _ryuApiService.ValidateCurrentAuthCodeAsync();
            if (isValid)
                return false;

            _nouzertGamesKeyState = NouzertGamesKeyState.Expired;
            UpdateRyuManifestKeyStatus();
            UpdateActionStates();
            return true;
        }

        private string GetBlockedNouzertGamesMessage()
        {
            if (!_accessGranted)
                return "Acesso bloqueado. Digite a chave de acesso em Configuracoes.";

            return _nouzertGamesKeyState switch
            {
                NouzertGamesKeyState.Expired => "Chave NouzertGames expirada. Adquira nova chave.",
                NouzertGamesKeyState.Validating => "Aguarde a validacao da chave NouzertGames.",
                _ => "Chave NouzertGames nao configurada."
            };
        }

        private void ApplyActionState(GameItem game)
        {
            game.ActionsEnabled = CanUseProtectedActions;
        }

        private void UpdateActionStates()
        {
            foreach (var game in GamesGrid.Items.OfType<GameItem>())
                ApplyActionState(game);

            foreach (var game in GeneratorResultsList.Items.OfType<GameItem>())
                ApplyActionState(game);
        }

        private void AuthCodeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Real-time validation could be added here
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            _logger.Clear();
            LogTextBox.Clear();
            SetStatusMessage("Log cleared", "Info");
        }

        #endregion

        private void SetStatusMessage(string message, string type)
        {
            if (StatusMessage == null)
            {
                _logger.Debug($"Status before UI ready [{type}]: {message}");
                return;
            }

            StatusMessage.Text = message;
            
            switch (type)
            {
                case "Success":
                    StatusMessage.Foreground = (Brush)FindResource("SuccessColor");
                    break;
                case "Error":
                    StatusMessage.Foreground = (Brush)FindResource("ErrorColor");
                    break;
                case "Warning":
                    StatusMessage.Foreground = (Brush)FindResource("WarningColor");
                    break;
                default:
                    StatusMessage.Foreground = (Brush)FindResource("TextSecondary");
                    break;
            }

            // Auto-clear after 5 seconds for non-error messages
            if (type != "Error")
            {
                _ = Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() =>
                    {
                        if (StatusMessage == null)
                            return;

                        if (StatusMessage.Text == message)
                        {
                            StatusMessage.Text = "Pronto";
                            StatusMessage.Foreground = (Brush)FindResource("TextSecondary");
                        }
                    }));
            }
        }

        #region SteamTools Methods

        /// <summary>
        /// Detects SteamTools path and updates the UI with status.
        /// </summary>
        private void DetectSteamTools()
        {
            // Load saved path first
            if (!string.IsNullOrEmpty(_config.SteamToolsPath))
            {
                _steamToolsService.SteamToolsPath = _config.SteamToolsPath;
                SteamToolsExeTextBox.Text = _config.SteamToolsPath;
            }
            else
            {
                // Try to auto-detect
                var detectedPath = _steamToolsService.DetectSteamToolsPath();
                if (!string.IsNullOrEmpty(detectedPath))
                {
                    SteamToolsExeTextBox.Text = detectedPath;
                    _configService.UpdateSteamToolsPath(detectedPath);
                    _logger.Info($"SteamTools auto-detected at: {detectedPath}");
                }
                else
                {
                    SteamToolsExeTextBox.Text = "Not found. Click Browse to locate SteamTools.";
                    _logger.Warning("SteamTools not detected automatically");
                }
            }

            // Update status UI
            UpdateSteamToolsStatusUI();
        }

        /// <summary>
        /// Updates the SteamTools status indicators (Settings text + header indicator).
        /// </summary>
        private void UpdateSteamToolsStatusUI()
        {
            var isRunning = _steamToolsService.IsSteamToolsRunning();
            var steamToolsPath = _steamToolsService.DetectSteamToolsPath();
            var isInstalled = !string.IsNullOrWhiteSpace(steamToolsPath) && File.Exists(steamToolsPath);

            if (isInstalled && steamToolsPath != null)
            {
                SteamToolsExeTextBox.Text = steamToolsPath;
                _configService.UpdateSteamToolsPath(steamToolsPath);
            }

            SteamToolsStatusIndicator.Foreground = isRunning
                ? (Brush?)(new BrushConverter().ConvertFromString("#107c10") ?? Brushes.Green)
                : (Brush?)(new BrushConverter().ConvertFromString("#e81123") ?? Brushes.Red);
            SteamToolsStatusIndicator.Text = isRunning ? "SteamTools Rodando" : "SteamTools nao funcionando";
            SteamToolsStatusIndicator.ToolTip = isRunning ? "SteamTools esta rodando" : "Clique para iniciar o SteamTools";

            SteamToolsRequirementPanel.BorderBrush = isInstalled
                ? (Brush)FindResource("SuccessColor")
                : (Brush)FindResource("ErrorColor");
            SteamToolsRequirementTitle.Text = isInstalled ? "Steamtools instalado" : "SteamTools obrigatorio";
            SteamToolsRequirementTitle.Foreground = isInstalled
                ? (Brush)FindResource("SuccessColor")
                : (Brush)FindResource("TextPrimary");
            SteamToolsRequirementInstruction.Visibility = isInstalled ? Visibility.Collapsed : Visibility.Visible;
            SteamToolsDownloadButton.Visibility = isInstalled ? Visibility.Collapsed : Visibility.Visible;

            // Update settings status text
            SteamToolsStatusText.Text = isRunning
                ? "Rodando"
                : isInstalled ? "Instalado" : "Nao instalado";
            SteamToolsStatusText.Foreground = isRunning || isInstalled
                ? (Brush?)(new BrushConverter().ConvertFromString("#107c10") ?? Brushes.Green)
                : (Brush?)(new BrushConverter().ConvertFromString("#e81123") ?? Brushes.Red);

            // Enable/disable Start button based on status
            StartSteamToolsButton.IsEnabled = !isRunning && isInstalled;
        }

        /// <summary>
        /// Handles the Browse button click for selecting the SteamTools executable.
        /// </summary>
        private void BrowseSteamToolsButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select SteamTools Executable",
                Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                DefaultExt = ".exe",
                CheckFileExists = true,
                Multiselect = false
            };

            // Set initial directory to last known path or Program Files
            if (!string.IsNullOrEmpty(_config.SteamToolsPath))
            {
                dialog.InitialDirectory = System.IO.Path.GetDirectoryName(_config.SteamToolsPath);
            }
            else
            {
                dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            }

            if (dialog.ShowDialog() == true)
            {
                var selectedPath = dialog.FileName;

                // Update service and config
                _steamToolsService.SteamToolsPath = selectedPath;
                _configService.UpdateSteamToolsPath(selectedPath);

                // Update UI
                SteamToolsExeTextBox.Text = selectedPath;
                _logger.Info($"SteamTools path set by user: {selectedPath}");

                // Update config cache
                _config = _configService.LoadConfig();

                // Refresh status
                UpdateSteamToolsStatusUI();
                SetStatusMessage("SteamTools path saved", "Success");
            }
        }

        /// <summary>
        /// Handles the Start SteamTools button click.
        /// </summary>
        private async void SteamToolsStatusIndicator_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            await StartSteamToolsFromUiAsync();
        }

        private async void StartSteamToolsButton_Click(object sender, RoutedEventArgs e)
        {
            await StartSteamToolsFromUiAsync();
        }

        private async System.Threading.Tasks.Task StartSteamToolsFromUiAsync()
        {
            StartSteamToolsButton.IsEnabled = false;
            StartSteamToolsButton.Content = "Iniciando...";
            SetStatusMessage("Iniciando SteamTools...", "Info");

            try
            {
                var success = await _steamToolsService.StartSteamToolsAsync();

                if (success)
                {
                    _logger.Info("SteamTools iniciado com sucesso");
                    SetStatusMessage("SteamTools iniciado com sucesso", "Success");

                    // Wait a bit and update status
                    await System.Threading.Tasks.Task.Delay(2000);
                    UpdateSteamToolsStatusUI();
                }
                else
                {
                    _logger.Error("Failed to start SteamTools");
                    SetStatusMessage("Falha ao iniciar SteamTools. Verifique o caminho em Configuracoes.", "Error");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Exception while starting SteamTools", ex);
                SetStatusMessage($"Erro: {ex.Message}", "Error");
            }
            finally
            {
                StartSteamToolsButton.Content = "Iniciar SteamTools";
                UpdateSteamToolsStatusUI(); // Refresh button state
            }
        }

        #endregion

        #region Pagination Methods

        /// <summary>
        /// Loads the initial catalog page on startup and after tab switch.
        /// </summary>
        private async System.Threading.Tasks.Task LoadCatalogPageAsync()
        {
            PaginationPanel.Visibility = Visibility.Visible;
            SetStatusMessage($"Loading catalog page {_currentPage}...", "Info");

            try
            {
                // Ajustado para receber os 4 elementos retornados pela API (usando _ para ignorar os que nÃ£o usamos)
                var (totalCount, games, _, _) = await _ryuApiService.LoadCatalogAsync(_currentPage, _pageSize);
                _totalCount = totalCount;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    GamesGrid.Items.Clear();

                    foreach (var game in games)
                    {
                        ApplyActionState(game);
                        GamesGrid.Items.Add(game);
                    }

                    // Update pagination UI
                    var totalPages = Math.Max(1, (int)Math.Ceiling((double)_totalCount / _pageSize));
                    TxtCurrentPage.Text = _currentPage.ToString();
                    TxtTotalPages.Text = $"of {totalPages}";
                    TxtTotalGames.Text = $"of {_totalCount:N0} games";

                    // Enable/disable navigation buttons
                    BtnPrevious.IsEnabled = _currentPage > 1;
                    BtnNext.IsEnabled = _currentPage < totalPages;

                    SetStatusMessage($"Page {_currentPage} of {totalPages} ({_totalCount:N0} games)", "Success");
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Error loading catalog page {_currentPage}", ex);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    SetStatusMessage($"Error loading catalog: {ex.Message}", "Error");
                });
            }
        }

        /// <summary>
        /// Handles the Previous Page button click.
        /// </summary>
        private async void OnPreviousPageClick(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                await LoadCatalogPageAsync();
            }
        }

        /// <summary>
        /// Handles the Next Page button click.
        /// </summary>
        private async void OnNextPageClick(object sender, RoutedEventArgs e)
        {
            _currentPage++;
            await LoadCatalogPageAsync();
        }

        private async void RefreshCatalogButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshCatalogButton.IsEnabled = false;
            SetStatusMessage("Atualizando catalogo...", "Info");

            try
            {
                await LoadCatalogPageAsync();
            }
            finally
            {
                RefreshCatalogButton.IsEnabled = true;
            }
        }

        private void RefreshLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshLibraryButton.IsEnabled = false;
            SetStatusMessage("Atualizando biblioteca local...", "Info");

            try
            {
                LoadInstalledGames();
                SetStatusMessage("Biblioteca atualizada", "Success");
            }
            finally
            {
                RefreshLibraryButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Handles the page size ComboBox selection change.
        /// </summary>
        private async void ComboPageSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || ComboPageSize == null)
                return;

            if (ComboPageSize.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out var size))
            {
                _pageSize = size;
                _currentPage = 1; // Reset to first page
                await LoadCatalogPageAsync();
            }
        }

        #endregion

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Save window preferences
            if (_config.Preferences != null)
            {
                _config.Preferences.WindowWidth = Width;
                _config.Preferences.WindowHeight = Height;
                _config.Preferences.IsMaximized = WindowState == WindowState.Maximized;
                _configService.SaveConfig(_config);
            }

            base.OnClosing(e);
        }
    }
}


