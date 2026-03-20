using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace CopilotTerminalFeed.Server;

/// <summary>
/// Local HTTP + WebSocket server that serves the xterm.js terminal UI
/// and manages ConPTY sessions via WebSocket connections.
/// </summary>
public sealed class TerminalServer
{
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _webContentPath;
    private readonly Timer _cleanupTimer;
    private readonly List<WebSocket> _activeWebSockets = new();
    private readonly object _wsListLock = new();
    private readonly DateTime _startedAt = DateTime.UtcNow;

    /// <summary>Auth token required on all API/WebSocket requests. Passed to the UI via query param.</summary>
    public string AuthToken { get; }
    public int Port { get; }
    public ServerConfig Config { get; }

    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    public TerminalServer(int port = 0, ServerConfig? config = null)
    {
        Config = config ?? ServerConfig.Load();
        Port = port == 0 ? FindAvailablePort() : port;
        AuthToken = GenerateToken();

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

        // Periodic cleanup of idle/dead sessions
        _cleanupTimer = new Timer(_ => CleanupSessions(), null, CleanupInterval, CleanupInterval);
    }

    /// <summary>The full URL to the terminal page, including the auth token.</summary>
    public string TerminalUrl => $"http://localhost:{Port}/terminal?token={AuthToken}";

    public async Task StartAsync()
    {
        _listener.Start();
        Log.Info($"Terminal server listening on http://localhost:{Port}/");
        Log.Info($"Auth token: {AuthToken}");

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

    public async Task StopAsync()
    {
        _cts.Cancel();
        _cleanupTimer.Dispose();

        // Gracefully close all active WebSocket connections
        List<WebSocket> sockets;
        lock (_wsListLock)
        {
            sockets = new List<WebSocket>(_activeWebSockets);
            _activeWebSockets.Clear();
        }

        var closeTasks = sockets
            .Where(ws => ws.State == WebSocketState.Open)
            .Select(async ws =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await ws.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Server shutting down", timeout.Token);
                }
                catch { }
            });
        await Task.WhenAll(closeTasks);

        _listener.Stop();

        foreach (var entry in _sessions.Values)
        {
            entry.Session.Dispose();
        }
        _sessions.Clear();
    }

    /// <summary>
    /// Creates a new ConPTY session with the specified command.
    /// Returns the session ID.
    /// </summary>
    public string CreateSession(string command = "cmd.exe")
    {
        // Validate command against allowlist
        var resolvedCommand = ResolveCommand(command);

        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var session = new ConPtySession(resolvedCommand);
        var entry = new SessionEntry(session, command);
        _sessions[sessionId] = entry;

        try
        {
            session.Start();
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(sessionId, out _);
            Log.Error($"Failed to start session for '{command}'", ex);
            throw;
        }

        // Wire up process exit to mark the entry as dead
        session.ProcessExited += () => entry.MarkExited();

        Log.Info($"Created session {sessionId} for command: {resolvedCommand}");
        return sessionId;
    }

    /// <summary>Removes a session and disposes its resources.</summary>
    public void DestroySession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var entry))
        {
            entry.Session.Dispose();
            Log.Info($"Destroyed session {sessionId}");
        }
    }

    private void CleanupSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, entry) in _sessions)
        {
            var idle = now - entry.LastActivity;
            if (!entry.Session.IsRunning || idle > SessionIdleTimeout)
            {
                DestroySession(id);
            }
        }
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
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");

            if (context.Request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            // Static page (terminal UI) — token checked via query param
            if (path is "/" or "/terminal")
            {
                if (!ValidateToken(context, allowQueryParam: true))
                {
                    Respond(response, 403, "Forbidden");
                    return;
                }
                await ServeFileAsync(response, "index.html", "text/html");
                return;
            }

            // Static assets don't need auth (CSS, JS, fonts loaded by the page)
            if (IsStaticAsset(path))
            {
                var fileName = path.TrimStart('/');
                await ServeFileAsync(response, fileName, GetContentType(fileName));
                return;
            }

            // All API/WS endpoints require auth
            if (!ValidateToken(context, allowQueryParam: true))
            {
                Respond(response, 403, "Forbidden");
                return;
            }

            switch (path)
            {
                case "/api/session":
                    await HandleCreateSessionAsync(context);
                    break;

                case "/api/sessions":
                    HandleListSessions(response);
                    break;

                case "/api/session/destroy":
                    await HandleDestroySessionAsync(context);
                    break;

                case "/api/health":
                    HandleHealth(response);
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
                    response.StatusCode = 404;
                    response.Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error handling {path}", ex);
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

        string command = Config.DefaultShell;
        if (context.Request.HasEntityBody)
        {
            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("command", out var cmdProp))
                {
                    command = cmdProp.GetString() ?? command;
                }
            }
            catch (JsonException) { }
        }

        try
        {
            var sessionId = CreateSession(command);
            var json = JsonSerializer.Serialize(new
            {
                sessionId,
                wsUrl = $"ws://localhost:{Port}/ws?session={sessionId}&token={AuthToken}",
                command
            });
            Respond(response, 200, json, "application/json");
        }
        catch (Exception ex)
        {
            var json = JsonSerializer.Serialize(new { error = ex.Message });
            Respond(response, 500, json, "application/json");
        }
    }

    private async Task HandleDestroySessionAsync(HttpListenerContext context)
    {
        var response = context.Response;
        string? sessionId = null;

        if (context.Request.HasEntityBody)
        {
            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("sessionId", out var prop))
                    sessionId = prop.GetString();
            }
            catch (JsonException) { }
        }

        if (sessionId is not null && _sessions.ContainsKey(sessionId))
        {
            DestroySession(sessionId);
            Respond(response, 200, "{\"ok\":true}", "application/json");
        }
        else
        {
            Respond(response, 404, "{\"error\":\"session not found\"}", "application/json");
        }
    }

    private void HandleListSessions(HttpListenerResponse response)
    {
        var sessions = _sessions.Select(kv => new
        {
            id = kv.Key,
            command = kv.Value.Command,
            running = kv.Value.Session.IsRunning,
            idleSeconds = (int)(DateTime.UtcNow - kv.Value.LastActivity).TotalSeconds
        });
        var json = JsonSerializer.Serialize(sessions);
        Respond(response, 200, json, "application/json");
    }

    private void HandleHealth(HttpListenerResponse response)
    {
        int activeWs;
        lock (_wsListLock) { activeWs = _activeWebSockets.Count; }

        var health = new
        {
            status = "ok",
            uptime = (int)(DateTime.UtcNow - _startedAt).TotalSeconds,
            sessions = new
            {
                total = _sessions.Count,
                running = _sessions.Values.Count(e => e.Session.IsRunning),
                exited = _sessions.Values.Count(e => e.HasExited),
            },
            activeWebSockets = activeWs,
            port = Port,
        };
        var json = JsonSerializer.Serialize(health);
        Respond(response, 200, json, "application/json");
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context)
    {
        var sessionId = context.Request.QueryString["session"];

        if (string.IsNullOrEmpty(sessionId) || !_sessions.TryGetValue(sessionId, out var entry))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        entry.TouchActivity();

        var wsContext = await context.AcceptWebSocketAsync(null);
        var ws = wsContext.WebSocket;

        lock (_wsListLock) { _activeWebSockets.Add(ws); }

        try
        {
            var handler = new WebSocketHandler(ws, entry.Session, () => entry.TouchActivity());
            await handler.RunAsync(_cts.Token);
        }
        finally
        {
            lock (_wsListLock) { _activeWebSockets.Remove(ws); }
            entry.TouchActivity();
        }
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

    private bool ValidateToken(HttpListenerContext context, bool allowQueryParam = false)
    {
        // Check Authorization header: "Bearer <token>"
        var authHeader = context.Request.Headers["Authorization"];
        if (authHeader is not null)
        {
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var headerToken = authHeader["Bearer ".Length..].Trim();
                if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(headerToken),
                    System.Text.Encoding.UTF8.GetBytes(AuthToken)))
                {
                    return true;
                }
            }
        }

        // Check query param
        if (allowQueryParam)
        {
            var queryToken = context.Request.QueryString["token"];
            if (queryToken is not null && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(queryToken),
                System.Text.Encoding.UTF8.GetBytes(AuthToken)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStaticAsset(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".js" or ".css" or ".png" or ".svg" or ".ico" or ".woff" or ".woff2" or ".ttf" or ".map";
    }

    private static void Respond(HttpListenerResponse response, int statusCode, string body, string contentType = "text/plain")
    {
        response.ContentType = contentType;
        response.StatusCode = statusCode;
        var buffer = System.Text.Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer);
        response.Close();
    }

    /// <summary>
    /// Resolves a user-friendly command name to the actual executable/args.
    /// Validates that the requested command is allowed.
    /// </summary>
    private string ResolveCommand(string command)
    {
        if (Config.AllowedCommands.Count > 0)
        {
            var baseCmd = command.Split(' ', 2)[0].ToLowerInvariant();
            if (!Config.AllowedCommands.Any(c => c.Equals(baseCmd, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Command '{baseCmd}' is not in the allowed commands list.");
            }
        }

        return command;
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
        ".map" => "application/json",
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

    private static string GenerateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    /// <summary>Tracks a session along with metadata for lifecycle management.</summary>
    private sealed class SessionEntry
    {
        public ConPtySession Session { get; }
        public string Command { get; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
        public bool HasExited { get; private set; }

        public SessionEntry(ConPtySession session, string command)
        {
            Session = session;
            Command = command;
        }

        public void TouchActivity() => LastActivity = DateTime.UtcNow;
        public void MarkExited() => HasExited = true;
    }
}
