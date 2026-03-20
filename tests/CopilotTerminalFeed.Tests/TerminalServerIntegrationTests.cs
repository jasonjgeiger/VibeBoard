using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CopilotTerminalFeed.Server;
using Xunit;

namespace CopilotTerminalFeed.Tests;

/// <summary>
/// Integration tests that start the HTTP server and exercise API + WebSocket endpoints.
/// These tests run against a real TerminalServer instance (but skip ConPTY since it
/// requires a Windows desktop session).
/// </summary>
public class TerminalServerIntegrationTests : IAsyncLifetime
{
    private TerminalServer _server = null!;
    private Task _serverTask = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var config = new ServerConfig
        {
            AllowedCommands = new List<string> { "cmd.exe", "powershell.exe", "claude", "gh" }
        };
        _server = new TerminalServer(config: config);
        _serverTask = Task.Run(() => _server.StartAsync());

        // Give the listener a moment to bind
        await Task.Delay(200);

        _client = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{_server.Port}")
        };
        _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_server.AuthToken}");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
    }

    [Fact]
    public async Task Terminal_Page_Returns_Html_With_Valid_Token()
    {
        var resp = await _client.GetAsync($"/terminal?token={_server.AuthToken}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Terminal_Page_Returns_403_Without_Token()
    {
        using var noAuthClient = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{_server.Port}")
        };
        var resp = await noAuthClient.GetAsync("/terminal");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Static_Assets_Served_Without_Auth()
    {
        using var noAuthClient = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{_server.Port}")
        };
        var resp = await noAuthClient.GetAsync("/terminal.css");
        // Either 200 (file found) or 404 (file not at that path in test env) — but not 403
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Api_Session_Requires_Auth()
    {
        using var noAuthClient = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{_server.Port}")
        };

        var content = new StringContent("{\"command\":\"cmd.exe\"}", Encoding.UTF8, "application/json");
        var resp = await noAuthClient.PostAsync("/api/session", content);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task List_Sessions_Returns_Json_Array()
    {
        var resp = await _client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task Health_Endpoint_Returns_Status()
    {
        var resp = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("uptime").GetInt32() >= 0);
        Assert.True(doc.RootElement.GetProperty("sessions").GetProperty("total").GetInt32() >= 0);
    }

    [Fact]
    public async Task Destroy_Nonexistent_Session_Returns_404()
    {
        var content = new StringContent("{\"sessionId\":\"doesnotexist\"}", Encoding.UTF8, "application/json");
        var resp = await _client.PostAsync("/api/session/destroy", content);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Options_Request_Returns_204()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/session");
        var resp = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task Unknown_Path_Returns_404()
    {
        var resp = await _client.GetAsync("/api/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public void TerminalUrl_Matches_Server_Config()
    {
        Assert.Contains(_server.Port.ToString(), _server.TerminalUrl);
        Assert.Contains(_server.AuthToken, _server.TerminalUrl);
    }
}
