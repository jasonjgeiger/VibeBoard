using System.Text;
using System.Text.Json;
using Xunit;

namespace CopilotTerminalFeed.Tests;

/// <summary>
/// Tests for the WebSocket JSON protocol messages (serialization/deserialization).
/// These tests validate the wire format without needing an actual WebSocket connection.
/// </summary>
public class WebSocketProtocolTests
{
    [Fact]
    public void Input_Message_Has_Correct_Shape()
    {
        var msg = JsonSerializer.Serialize(new { type = "input", data = "ls\n" });
        using var doc = JsonDocument.Parse(msg);
        var root = doc.RootElement;

        Assert.Equal("input", root.GetProperty("type").GetString());
        Assert.Equal("ls\n", root.GetProperty("data").GetString());
    }

    [Fact]
    public void Resize_Message_Has_Correct_Shape()
    {
        var msg = JsonSerializer.Serialize(new { type = "resize", cols = 120, rows = 30 });
        using var doc = JsonDocument.Parse(msg);
        var root = doc.RootElement;

        Assert.Equal("resize", root.GetProperty("type").GetString());
        Assert.Equal(120, root.GetProperty("cols").GetInt32());
        Assert.Equal(30, root.GetProperty("rows").GetInt32());
    }

    [Fact]
    public void Output_Message_Data_Is_Valid_Base64()
    {
        var original = "Hello, terminal!\r\n"u8.ToArray();
        var base64 = Convert.ToBase64String(original);
        var msg = $"{{\"type\":\"output\",\"data\":\"{base64}\"}}";

        using var doc = JsonDocument.Parse(msg);
        var data = doc.RootElement.GetProperty("data").GetString()!;
        var decoded = Convert.FromBase64String(data);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Exit_Message_Is_Simple()
    {
        var msg = "{\"type\":\"exit\"}";
        using var doc = JsonDocument.Parse(msg);
        Assert.Equal("exit", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void Ping_Pong_Protocol()
    {
        var ping = "{\"type\":\"ping\"}";
        using var doc = JsonDocument.Parse(ping);
        Assert.Equal("ping", doc.RootElement.GetProperty("type").GetString());

        var pong = "{\"type\":\"pong\"}";
        using var doc2 = JsonDocument.Parse(pong);
        Assert.Equal("pong", doc2.RootElement.GetProperty("type").GetString());
    }
}
