using System.Text.Json;

namespace CopilotTerminalFeed.Server;

/// <summary>
/// Configuration for the terminal server. Loaded from config.json if present,
/// otherwise uses sensible defaults.
/// </summary>
public sealed class ServerConfig
{
    public string DefaultShell { get; set; } = "cmd.exe";
    public int Port { get; set; } = 0;
    public int FontSize { get; set; } = 14;
    public string FontFamily { get; set; } = "'Cascadia Code', 'Consolas', 'Courier New', monospace";
    public string Theme { get; set; } = "catppuccin-mocha";
    public int SessionIdleTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Allowlist of commands that can be launched. Empty = allow all.
    /// When populated, only the base executable name is checked.
    /// </summary>
    public List<string> AllowedCommands { get; set; } = new()
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wsl.exe",
        "claude", "gh"
    };

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CopilotTerminalFeed", "config.json");

    public static ServerConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<ServerConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new ServerConfig();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load config from {ConfigPath}: {ex.Message}");
        }

        return new ServerConfig();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
