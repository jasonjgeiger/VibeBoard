using CopilotTerminalFeed.Server;
using Xunit;

namespace CopilotTerminalFeed.Tests;

public class TerminalServerTests
{
    [Fact]
    public void Constructor_Assigns_Random_Port_When_Zero()
    {
        var server = new TerminalServer(port: 0);
        Assert.True(server.Port > 0);
        Assert.True(server.Port < 65536);
    }

    [Fact]
    public void Constructor_Uses_Specified_Port()
    {
        var server = new TerminalServer(port: 19876);
        Assert.Equal(19876, server.Port);
    }

    [Fact]
    public void AuthToken_Is_Generated_And_NonEmpty()
    {
        var server = new TerminalServer();
        Assert.False(string.IsNullOrWhiteSpace(server.AuthToken));
        Assert.Equal(32, server.AuthToken.Length); // 16 bytes hex = 32 chars
    }

    [Fact]
    public void AuthToken_Is_Unique_Per_Instance()
    {
        var server1 = new TerminalServer();
        var server2 = new TerminalServer();
        Assert.NotEqual(server1.AuthToken, server2.AuthToken);
    }

    [Fact]
    public void TerminalUrl_Contains_Port_And_Token()
    {
        var server = new TerminalServer(port: 12345);
        Assert.Contains("12345", server.TerminalUrl);
        Assert.Contains(server.AuthToken, server.TerminalUrl);
        Assert.StartsWith("http://localhost:12345/terminal?token=", server.TerminalUrl);
    }

    [Fact]
    public void Config_Is_Loaded()
    {
        var server = new TerminalServer();
        Assert.NotNull(server.Config);
        Assert.Equal("cmd.exe", server.Config.DefaultShell);
    }
}
