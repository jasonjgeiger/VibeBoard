using System.Collections.Concurrent;
using System.Net;

namespace CopilotTerminalFeed.Server;

/// <summary>
/// Local HTTP + WebSocket server that serves the xterm.js terminal UI
/// and manages ConPTY sessions via WebSocket connections.
/// </summary>
public sealed class TerminalServer
{
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<string, ConPtySession> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _webContentPath;

    public int Port { get; }

    public TerminalServer(int port = 0)
    {
        // Use a dynamic port if 0 is specified, otherwise use the given port
        Port = port == 0 ? FindAvailablePort() : port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");

        // Resolve web content path relative to the executable
        var baseDir = AppContext.BaseDirectory;
        _webContentPath = Path.Combine(baseDir, "WebContent");

        // Fallback: check sibling web directory (development layout)
        if (!Directory.Exists(_webContentPath))
        {
            _webContentPath = Path.Combine(baseDir, "..", "..", "..", "..", "..", "web");
            _webContentPath = Path.GetFullPath(_webContentPath);
        }
    }

    public async Task StartAsync()
    {
        _listener.Start();
        Console.WriteLine($"Terminal server listening on http://localhost:{Port}/");

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (HttpListenerException) when (_cts.Token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    public Task StopAsync()
    {
        _cts.Cancel();
        _listener.Stop();

        // Dispose all active PTY sessions
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a new ConPTY session with the specified command.
    /// Returns the session ID.
    /// </summary>
    public string CreateSession(string command = "cmd.exe")
    {
        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var session = new ConPtySession(command);
        _sessions[sessionId] = session;
        session.Start();
        Console.WriteLine($"Created session {sessionId} for command: {command}");
        return sessionId;
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var response = context.Response;

        try
        {
            // CORS headers for local development
            response.Headers.Add("Access-Control-Allow-Origin", "http://localhost");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (context.Request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            switch (path)
            {
                case "/":
                case "/terminal":
                    await ServeFileAsync(response, "index.html", "text/html");
                    break;

                case "/terminal.js":
                    await ServeFileAsync(response, "terminal.js", "application/javascript");
                    break;

                case "/terminal.css":
                    await ServeFileAsync(response, "terminal.css", "text/css");
                    break;

                case "/api/session":
                    await HandleCreateSessionAsync(context);
                    break;

                case "/api/sessions":
                    await HandleListSessionsAsync(response);
                    break;

                case "/ws":
                    if (context.Request.IsWebSocketRequest)
                    {
                        await HandleWebSocketAsync(context);
                    }
                    else
                    {
                        response.StatusCode = 400;
                        response.Close();
                    }
                    break;

                default:
                    // Try to serve as static file
                    var fileName = path.TrimStart('/');
                    var filePath = Path.Combine(_webContentPath, fileName);
                    if (File.Exists(filePath))
                    {
                        var contentType = GetContentType(fileName);
                        await ServeFileAsync(response, fileName, contentType);
                    }
                    else
                    {
                        response.StatusCode = 404;
                        response.Close();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error handling {path}: {ex.Message}");
            try
            {
                response.StatusCode = 500;
                response.Close();
            }
            catch { }
        }
    }

    private async Task HandleCreateSessionAsync(HttpListenerContext context)
    {
        var response = context.Response;

        string command = "cmd.exe";
        if (context.Request.HasEntityBody)
        {
            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            // Simple JSON parsing for {"command": "..."}
            var cmdStart = body.IndexOf("\"command\"");
            if (cmdStart >= 0)
            {
                var colonIdx = body.IndexOf(':', cmdStart);
                var valueStart = body.IndexOf('"', colonIdx + 1) + 1;
                var valueEnd = body.IndexOf('"', valueStart);
                if (valueStart > 0 && valueEnd > valueStart)
                {
                    command = body[valueStart..valueEnd];
                }
            }
        }

        var sessionId = CreateSession(command);

        response.ContentType = "application/json";
        response.StatusCode = 200;
        var json = $"{{\"sessionId\":\"{sessionId}\",\"wsUrl\":\"ws://localhost:{Port}/ws?session={sessionId}\"}}";
        var buffer = System.Text.Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    private async Task HandleListSessionsAsync(HttpListenerResponse response)
    {
        var ids = _sessions.Keys.ToArray();
        var json = "[" + string.Join(",", ids.Select(id => $"\"{id}\"")) + "]";

        response.ContentType = "application/json";
        response.StatusCode = 200;
        var buffer = System.Text.Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context)
    {
        var sessionId = context.Request.QueryString["session"];

        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var session))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        var wsContext = await context.AcceptWebSocketAsync(null);
        var handler = new WebSocketHandler(wsContext.WebSocket, session);
        await handler.RunAsync(_cts.Token);
    }

    private async Task ServeFileAsync(HttpListenerResponse response, string fileName, string contentType)
    {
        var filePath = Path.Combine(_webContentPath, fileName);
        if (!File.Exists(filePath))
        {
            response.StatusCode = 404;
            response.Close();
            return;
        }

        var content = await File.ReadAllBytesAsync(filePath);
        response.ContentType = contentType;
        response.StatusCode = 200;
        response.ContentLength64 = content.Length;
        await response.OutputStream.WriteAsync(content);
        response.Close();
    }

    private static string GetContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".html" => "text/html",
        ".js" => "application/javascript",
        ".css" => "text/css",
        ".json" => "application/json",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        _ => "application/octet-stream"
    };

    private static int FindAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
