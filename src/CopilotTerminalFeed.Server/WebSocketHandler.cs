using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CopilotTerminalFeed.Server;

/// <summary>
/// Bridges a WebSocket connection to a ConPTY session, relaying terminal I/O
/// between the xterm.js frontend and the PTY backend.
///
/// Protocol:
///   Client -> Server: {"type":"input","data":"..."} — keyboard input
///   Client -> Server: {"type":"resize","cols":N,"rows":N} — terminal resize
///   Server -> Client: {"type":"output","data":"..."} — terminal output (base64)
///   Server -> Client: {"type":"exit","code":N} — process exited with code
///   Server -> Client: {"type":"error","message":"..."} — error message
///   Server -> Client: {"type":"replay","data":"..."} — buffered output replay (base64)
/// </summary>
public sealed class WebSocketHandler
{
    private readonly WebSocket _webSocket;
    private readonly ConPtySession _session;
    private readonly Action? _onActivity;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationToken _ct;

    public WebSocketHandler(WebSocket webSocket, ConPtySession session, Action? onActivity = null)
    {
        _webSocket = webSocket;
        _session = session;
        _onActivity = onActivity;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _ct = ct;
        _session.OutputReceived += OnPtyOutput;
        _session.ProcessExited += OnProcessExited;

        try
        {
            // Send buffered output so the client sees recent history
            await SendReplayBuffer();

            await ReceiveLoop(ct);
        }
        finally
        {
            _session.OutputReceived -= OnPtyOutput;
            _session.ProcessExited -= OnProcessExited;
            _sendLock.Dispose();
        }
    }

    private async Task SendReplayBuffer()
    {
        var replay = _session.GetReplayBuffer();
        if (replay.Length == 0) return;

        try
        {
            var base64 = Convert.ToBase64String(replay);
            var message = $"{{\"type\":\"replay\",\"data\":\"{base64}\"}}";
            await SendMessageAsync(message);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
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
                _onActivity?.Invoke();
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

                case "ping":
                    await SendMessageAsync("{\"type\":\"pong\"}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            Log.Warn($"Invalid WebSocket message: {ex.Message}");
        }
    }

    private void OnPtyOutput(byte[] data)
    {
        if (_webSocket.State != WebSocketState.Open) return;
        _onActivity?.Invoke();

        // Fire-and-forget but with error handling — avoid async void
        _ = SendOutputSafe(data);
    }

    private async Task SendOutputSafe(byte[] data)
    {
        try
        {
            var base64 = Convert.ToBase64String(data);
            var message = $"{{\"type\":\"output\",\"data\":\"{base64}\"}}";
            await SendMessageAsync(message);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Error("Unexpected error sending PTY output to WebSocket", ex);
        }
    }

    private void OnProcessExited(int exitCode)
    {
        if (_webSocket.State != WebSocketState.Open) return;

        _ = SendExitSafe(exitCode);
    }

    private async Task SendExitSafe(int exitCode)
    {
        try
        {
            await SendMessageAsync($"{{\"type\":\"exit\",\"code\":{exitCode}}}");
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Error("Unexpected error sending exit message to WebSocket", ex);
        }
    }

    /// <summary>Thread-safe WebSocket send with a semaphore to prevent concurrent writes.</summary>
    private async Task SendMessageAsync(string message)
    {
        await _sendLock.WaitAsync(_ct);
        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, _ct);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
