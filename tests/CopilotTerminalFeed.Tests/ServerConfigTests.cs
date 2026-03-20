using CopilotTerminalFeed.Server;
using Xunit;

namespace CopilotTerminalFeed.Tests;

public class ServerConfigTests
{
    [Fact]
    public void Default_Config_Has_Sensible_Defaults()
    {
        var config = new ServerConfig();

        Assert.Equal("cmd.exe", config.DefaultShell);
        Assert.Equal(0, config.Port);
        Assert.Equal(14, config.FontSize);
        Assert.Equal(30, config.SessionIdleTimeoutMinutes);
        Assert.Contains("cmd.exe", config.AllowedCommands);
        Assert.Contains("claude", config.AllowedCommands);
        Assert.Contains("gh", config.AllowedCommands);
    }

    [Fact]
    public void Load_Returns_Default_When_No_File_Exists()
    {
        var config = ServerConfig.Load();
        Assert.NotNull(config);
        Assert.Equal("cmd.exe", config.DefaultShell);
    }

    [Fact]
    public void AllowedCommands_Contains_Expected_Shells()
    {
        var config = new ServerConfig();

        Assert.Contains("powershell.exe", config.AllowedCommands);
        Assert.Contains("pwsh.exe", config.AllowedCommands);
        Assert.Contains("wsl.exe", config.AllowedCommands);
    }
}
