using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace NouzertGames.Services
{
    /// <summary>
    /// Service responsible for managing the SteamTools external application.
    /// SteamTools (https://steamtools.net/) is required for proper manifest/Lua injection.
    /// Provides process detection, startup, path management, and .lnk shortcut resolution.
    /// </summary>
    public class SteamToolsService
    {
        private readonly LoggerService _logger;
        private string? _steamToolsPath;

        // Common SteamTools executable names to search for
        private static readonly string[] PossibleExecutables = { "SteamTools.exe", "STFrame.exe" };

        public SteamToolsService(LoggerService logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gets or sets the path to the SteamTools executable.
        /// When set, automatically resolves .lnk shortcuts to the real .exe path.
        /// Validates that the resolved file exists.
        /// </summary>
        public string? SteamToolsPath
        {
            get => _steamToolsPath;
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    _steamToolsPath = null;
                    return;
                }

                // Resolve shortcut if it's a .lnk file
                var resolvedPath = ResolveShortcut(value);

                if (File.Exists(resolvedPath))
                {
                    _steamToolsPath = resolvedPath;
                    _logger.Info($"SteamTools path set: {resolvedPath}");
                }
                else if (File.Exists(value))
                {
                    _steamToolsPath = value;
                    _logger.Info($"SteamTools path set: {value}");
                }
                else
                {
                    _logger.Warning($"SteamTools executable not found at: {value}");
                }
            }
        }

        /// <summary>
        /// Checks if SteamTools is currently running by searching for known process names.
        /// </summary>
        /// <returns>True if any SteamTools process is running, false otherwise</returns>
        public bool IsSteamToolsRunning()
        {
            try
            {
                // Check for SteamTools process
                var processes = Process.GetProcessesByName("SteamTools");
                if (processes.Length > 0)
                {
                    _logger.Debug("SteamTools process detected (SteamTools.exe)");
                    return true;
                }

                // Also check for STFrame process (alternative name)
                processes = Process.GetProcessesByName("STFrame");
                if (processes.Length > 0)
                {
                    _logger.Debug("SteamTools process detected (STFrame.exe)");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to check if SteamTools is running", ex);
                return false;
            }
        }

        /// <summary>
        /// Attempts to auto-detect the SteamTools installation path.
        /// Searches dynamically using environment variables (no hardcoded usernames).
        /// Checks: Start Menu shortcuts → Program Files → PATH → Common locations.
        /// </summary>
        /// <returns>The detected path, or null if not found</returns>
        public string? DetectSteamToolsPath()
        {
            // If already set and valid, return it
            if (!string.IsNullOrEmpty(_steamToolsPath) && File.Exists(_steamToolsPath))
                return _steamToolsPath;

            // 1. Search dynamically in the current user's Start Menu (no hardcoded usernames)
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var startMenuPaths = new[]
                {
                    Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs\SteamTools\SteamTools.lnk"),
                    Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs\SteamTools\STFrame.lnk"),
                    Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs\SteamTools\SteamTools\SteamTools.lnk")
                };

                foreach (var lnkPath in startMenuPaths)
                {
                    if (File.Exists(lnkPath))
                    {
                        var realPath = ResolveShortcut(lnkPath);
                        if (File.Exists(realPath))
                        {
                            _steamToolsPath = realPath;
                            _logger.Info($"SteamTools detected via Start Menu shortcut: {realPath}");
                            return realPath;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Error searching Start Menu for SteamTools: {ex.Message}");
            }

            // 2. Also check All Users / Public Start Menu
            try
            {
                var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                var commonMenuPath = Path.Combine(commonAppData, @"Microsoft\Windows\Start Menu\Programs\SteamTools\SteamTools.lnk");
                if (File.Exists(commonMenuPath))
                {
                    var realPath = ResolveShortcut(commonMenuPath);
                    if (File.Exists(realPath))
                    {
                        _steamToolsPath = realPath;
                        _logger.Info($"SteamTools detected via Common Start Menu: {realPath}");
                        return realPath;
                    }
                }
            }
            catch { }

            // 3. Check Program Files directories dynamically
            var programFiles = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            foreach (var pf in programFiles)
            {
                if (string.IsNullOrEmpty(pf)) continue;

                try
                {
                    var steamToolsDir = Path.Combine(pf, "SteamTools");
                    if (Directory.Exists(steamToolsDir))
                    {
                        foreach (var exeName in PossibleExecutables)
                        {
                            var exePath = Path.Combine(steamToolsDir, exeName);
                            if (File.Exists(exePath))
                            {
                                _steamToolsPath = exePath;
                                _logger.Info($"SteamTools detected in Program Files: {exePath}");
                                return exePath;
                            }
                        }
                    }
                }
                catch { }
            }

            // 4. Try to find it by searching PATH
            foreach (var exeName in PossibleExecutables)
            {
                try
                {
                    var foundPath = FindExecutableInPath(exeName);
                    if (!string.IsNullOrEmpty(foundPath))
                    {
                        _steamToolsPath = foundPath;
                        _logger.Info($"SteamTools detected via PATH: {foundPath}");
                        return foundPath;
                    }
                }
                catch { }
            }

            _logger.Warning("SteamTools not found in any location");
            return null;
        }

        /// <summary>
        /// Starts the SteamTools process using the configured executable path.
        /// </summary>
        /// <returns>True if the process was started successfully, false otherwise</returns>
        public bool StartSteamTools()
        {
            var exePath = _steamToolsPath;

            // Try to detect if not set
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                exePath = DetectSteamToolsPath();
            }

            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                _logger.Error("Cannot start SteamTools: executable not found");
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath)
                };

                Process.Start(startInfo);
                _logger.Info($"SteamTools process started: {exePath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start SteamTools process: {exePath}", ex);
                return false;
            }
        }

        /// <summary>
        /// Starts the SteamTools process asynchronously.
        /// </summary>
        /// <returns>True if the process was started successfully, false otherwise</returns>
        public async Task<bool> StartSteamToolsAsync()
        {
            return await Task.Run(() => StartSteamTools());
        }

        /// <summary>
        /// Resolves a .lnk shortcut file to its real target path.
        /// Uses the Windows Shell API (IWshRuntimeLibrary) via COM.
        /// If the file is not a .lnk, returns the original path unchanged.
        /// </summary>
        /// <param name="path">The shortcut or file path to resolve</param>
        /// <returns>The real target path if resolved, or the original path</returns>
        public static string ResolveShortcut(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            // Only process .lnk files
            if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                return path;

            try
            {
                // Use the Windows Script Host Shell to resolve the shortcut
                // This works via COM interop without needing additional NuGet packages
                var shellType = Type.GetTypeFromProgID("Wscript.Shell");
                if (shellType == null)
                {
                    // Fallback: try manual parsing if COM fails
                    return TryParseLnkFile(path) ?? path;
                }

                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null)
                    return path;

                dynamic? shortcut = shell.CreateShortcut(path);
                if (shortcut == null)
                    return path;

                string? targetPath = shortcut.TargetPath;

                // Clean up COM objects
                Marshal.ReleaseComObject(shortcut);
                Marshal.ReleaseComObject(shell);

                if (!string.IsNullOrEmpty(targetPath))
                {
                    return targetPath;
                }

                return path;
            }
            catch (Exception ex)
            {
                // Fallback to manual parsing
                try
                {
                    return TryParseLnkFile(path) ?? path;
                }
                catch
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to resolve shortcut {path}: {ex.Message}");
                    return path;
                }
            }
        }

        /// <summary>
        /// Manual fallback parser for .lnk files (Shell Link Binary Format).
        /// Extracts the target path from the raw .lnk file bytes.
        /// This is a simplified parser that works for standard shortcuts.
        /// </summary>
        /// <param name="lnkPath">Path to the .lnk file</param>
        /// <returns>The target path if found, null otherwise</returns>
        private static string? TryParseLnkFile(string lnkPath)
        {
            try
            {
                var bytes = File.ReadAllBytes(lnkPath);
                if (bytes.Length < 0x4C)
                    return null;

                // Check for LNK header magic: 4C 00 00 00 01 14 02 00
                if (bytes[0] != 0x4C) return null;

                // Read the number of link target identifiers (at offset 0x4C)
                int flags = BitConverter.ToInt32(bytes, 0x14);
                bool hasTargetIdList = (flags & 0x01) != 0;
                bool hasLinkInfo = (flags & 0x02) != 0;
                bool hasName = (flags & 0x04) != 0;
                bool hasRelativePath = (flags & 0x08) != 0;
                bool hasWorkingDir = (flags & 0x10) != 0;
                bool hasArguments = (flags & 0x20) != 0;
                bool hasIconLocation = (flags & 0x40) != 0;

                int offset = 0x4C;

                // Skip the target ID list if present
                if (hasTargetIdList)
                {
                    int idListSize = BitConverter.ToInt16(bytes, offset);
                    offset += 2 + idListSize;
                }

                // Read link info if present
                if (hasLinkInfo)
                {
                    int linkInfoSize = BitConverter.ToInt32(bytes, offset);
                    if (linkInfoSize >= 0x1C)
                    {
                        int linkInfoOffset = BitConverter.ToInt32(bytes, offset + 0x0C);
                        if (linkInfoOffset > 0 && linkInfoOffset < bytes.Length)
                        {
                            // Read the local base path (volume mount point or drive letter)
                            int localBasePathOffset = BitConverter.ToInt32(bytes, offset + linkInfoOffset + 0x10);
                            if (localBasePathOffset > 0)
                            {
                                int absPath = linkInfoOffset + localBasePathOffset;
                                string basePath = ReadNullTerminatedString(bytes, offset + absPath);
                                if (!string.IsNullOrEmpty(basePath))
                                {
                                    // Common NetHood paths get cleaned
                                    var cleanPath = basePath.Replace("\\\\", "\\");
                                    if (File.Exists(cleanPath))
                                        return cleanPath;

                                    // Try with common base paths
                                    var driveLetter = Path.GetPathRoot(cleanPath);
                                    if (!string.IsNullOrEmpty(driveLetter))
                                    {
                                        var commonBasePath = BitConverter.ToInt32(bytes, offset + linkInfoOffset + 0x14);
                                        if (commonBasePath > 0)
                                        {
                                            string commonPath = ReadNullTerminatedString(bytes, offset + linkInfoOffset + commonBasePath);
                                            if (!string.IsNullOrEmpty(commonPath))
                                            {
                                                cleanPath = Path.Combine(driveLetter, commonPath.TrimStart('\\'));
                                                if (File.Exists(cleanPath))
                                                    return cleanPath;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    offset += linkInfoSize;
                }

                // Try to read the relative path (simpler and more reliable)
                if (hasRelativePath && offset < bytes.Length)
                {
                    int stringSize = BitConverter.ToInt16(bytes, offset);
                    if (stringSize > 0 && stringSize < bytes.Length - offset - 2)
                    {
                        var relativePath = Encoding.Unicode.GetString(bytes, offset + 2, stringSize);
                        if (!string.IsNullOrEmpty(relativePath))
                        {
                            var fullPath = Path.GetFullPath(Path.Combine(
                                Path.GetDirectoryName(lnkPath) ?? "",
                                relativePath));
                            if (File.Exists(fullPath))
                                return fullPath;
                        }
                    }
                }
            }
            catch
            {
                // Silently fail on parsing errors
            }

            return null;
        }

        /// <summary>
        /// Reads a null-terminated Unicode string from a byte array at the specified offset.
        /// </summary>
        private static string ReadNullTerminatedString(byte[] data, int offset)
        {
            if (offset < 0 || offset >= data.Length)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = offset; i < data.Length - 1; i += 2)
            {
                char c = BitConverter.ToChar(data, i);
                if (c == '\0') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Searches for an executable in the system PATH and common directories.
        /// </summary>
        /// <param name="executableName">The executable name to find</param>
        /// <returns>The full path if found, null otherwise</returns>
        private static string? FindExecutableInPath(string executableName)
        {
            // Check PATH environment variable
            var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator);
            if (paths != null)
            {
                foreach (var path in paths)
                {
                    try
                    {
                        var fullPath = Path.Combine(path.Trim(), executableName);
                        if (File.Exists(fullPath))
                            return fullPath;
                    }
                    catch { }
                }
            }

            return null;
        }
    }
}
