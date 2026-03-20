using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CopilotTerminalFeed.Server;

/// <summary>
/// Bridges a WebSocket connection to a ConPTY session, relaying terminal I/O
/// between the xterm.js frontend and the PTY backend.
///
/// Protocol:
///   Client → Server: {"type":"input","data":"..."} — keyboard input
///   Client → Server: {"type":"resize","cols":N,"rows":N} — terminal resize
///   Server → Client: {"type":"output","data":"..."} — terminal output (base64)
///   Server → Client: {"type":"exit"} — process exited
/// </summary>
public sealed class WebSocketHandler
{
    private readonly WebSocket _webSocket;
    private readonly ConPtySession _session;

    public WebSocketHandler(WebSocket webSocket, ConPtySession session)
    {
        _webSocket = webSocket;
        _session = session;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Wire up PTY output → WebSocket
        _session.OutputReceived += OnPtyOutput;
        _session.ProcessExited += OnProcessExited;

        try
        {
            await ReceiveLoop(ct);
        }
        finally
        {
            _session.OutputReceived -= OnPtyOutput;
            _session.ProcessExited -= OnProcessExited;
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[8192];

        while (_webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            using var ms = new MemoryStream();

            do
            {
                result = await _webSocket.ReceiveAsync(buffer, ct);
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(ms.ToArray());
                await HandleMessage(json, ct);
            }
        }
    }

    private async Task HandleMessage(string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "input":
                    var data = root.GetProperty("data").GetString();
                    if (data is not null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(data);
                        await _session.WriteAsync(bytes, ct);
                    }
                    break;

                case "resize":
                    var cols = root.GetProperty("cols").GetInt16();
                    var rows = root.GetProperty("rows").GetInt16();
                    _session.Resize(cols, rows);
                    break;
            }
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Invalid WebSocket message: {ex.Message}");
        }
    }

    private async void OnPtyOutput(byte[] data)
    {
        if (_webSocket.State != WebSocketState.Open) return;

        try
        {
            var base64 = Convert.ToBase64String(data);
            var message = $"{{\"type\":\"output\",\"data\":\"{base64}\"}}";
            var bytes = Encoding.UTF8.GetBytes(message);
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // Client disconnected
        }
    }

    private async void OnProcessExited()
    {
        if (_webSocket.State != WebSocketState.Open) return;

        try
        {
            var message = "{\"type\":\"exit\"}"u8.ToArray();
            await _webSocket.SendAsync(message, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (WebSocketException) { }
    }
}
