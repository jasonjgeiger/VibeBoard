# CopilotTerminalFeed

A Windows App SDK feed provider that embeds an interactive terminal in the Windows Widget Board. Targets **Claude Code CLI** and **GitHub Copilot** as primary commands, but works as a general-purpose PTY terminal.

## Architecture

Three components, MSIX-packaged:

| Component | Description |
|---|---|
| **CopilotTerminalFeed** | WinUI3 app registering as a Feed Provider via `IFeedProvider` COM interface. Sends an Adaptive Card with a `ContentUri` pointing to the local server. |
| **CopilotTerminalFeed.Server** | Background HTTP + WebSocket server. Manages ConPTY sessions and relays terminal I/O. Authenticated with a per-launch token. |
| **web/** | xterm.js terminal UI served as a local webpage. Supports multiple session tabs, reconnection, search, settings, and Catppuccin Mocha theme. |

```
Widget Board  ──iframe──>  localhost:{port}/terminal?token=...
                                  │
                            HTTP server
                            (static files + REST API)
                                  │
                           WebSocket /ws
                                  │
                         ConPTY ──> cmd.exe / claude / gh copilot
```

## Features

- **Multi-tab terminals** — run multiple concurrent sessions (Ctrl+T / Ctrl+W)
- **Claude Code & Copilot** — one-click launch buttons in the toolbar
- **In-terminal search** — Ctrl+F with next/previous navigation
- **Clipboard** — Ctrl+Shift+C to copy, Ctrl+Shift+V to paste
- **Settings panel** — configurable font size, default shell, scrollback, cursor style
- **Session persistence** — tabs survive page reloads by reconnecting to running sessions
- **Output replay** — new/reconnecting clients see recent terminal output (64KB ring buffer)
- **Process exit codes** — displayed when a terminal process exits
- **Health monitoring** — `GET /api/health` returns uptime, session counts, WebSocket stats
- **Structured logging** — timestamped, leveled log output for diagnostics
- **Catppuccin Mocha** — dark theme with accessible contrast ratios

## Prerequisites

- **Windows 11** (build 22621 or later)
- **.NET 8 SDK** (with Windows App SDK workload)
- **Visual Studio 2022** (17.8+) or the `dotnet` CLI
- **Windows App SDK 1.5+** runtime
- Optional: [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code), [GitHub Copilot CLI](https://docs.github.com/en/copilot/using-github-copilot/using-github-copilot-in-the-command-line) (`gh copilot`)

## Building

### Via Visual Studio

1. Open `CopilotTerminalFeed.sln`
2. Set the startup project to **CopilotTerminalFeed**
3. Select **Debug | x64**
4. Build and run (F5)

### Via CLI

```powershell
# Restore and build
dotnet build CopilotTerminalFeed.sln -c Debug -p:Platform=x64

# Run tests
dotnet test tests/CopilotTerminalFeed.Tests/CopilotTerminalFeed.Tests.csproj -c Debug -p:Platform=x64
```

## Testing the App on Windows

### 1. Quick test (terminal server only, no Widget Board)

The fastest way to verify the terminal works without MSIX packaging:

```powershell
cd src/CopilotTerminalFeed
dotnet run -c Debug -p:Platform=x64
```

This prints the terminal URL with token:
```
Terminal URL: http://localhost:54321/terminal?token=abc123...
```

Open that URL in a browser. You should see:
- A toolbar with Claude / Copilot / Shell buttons
- A tab bar with a session auto-started
- A working terminal where you can type commands

Try:
- Click **Claude** to open a `claude` session (requires Claude Code installed)
- Click **Copilot** to open a `gh copilot` session (requires GitHub CLI + Copilot extension)
- Click **+** in the tab bar to open additional shell tabs
- Press **Ctrl+F** to search terminal output
- Press **Ctrl+T** / **Ctrl+W** to open / close tabs
- Click the **gear icon** to open settings

### 2. Test with Widget Board (MSIX packaging required)

To register as a feed provider in the actual Widget Board:

```powershell
# Create a self-signed certificate for dev
New-SelfSignedCertificate -Type Custom -Subject "CN=CopilotTerminalFeed" `
  -KeyUsage DigitalSignature -FriendlyName "CopilotTerminalFeed Dev" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

# Build the MSIX package
dotnet publish src/CopilotTerminalFeed/CopilotTerminalFeed.csproj `
  -c Debug -p:Platform=x64 `
  -p:AppxPackageDir=.\AppPackages\ `
  -p:GenerateAppxPackageOnBuild=true

# Install the package (Developer Mode must be enabled)
# Settings > Update & Security > For developers > Developer Mode
Add-AppxPackage -Path .\AppPackages\CopilotTerminalFeed_1.0.0.0_x64_Debug.msix

# The feed should now appear in the Widget Board (Win+W)
```

### 3. Test WebSocket reconnection

1. Start the app and open the terminal URL in a browser
2. Start a shell session and run a few commands
3. Kill and restart the server process
4. The browser tab should automatically reconnect (status bar shows "Reconnecting...")
5. After reconnection, recent output is replayed from the server buffer

### 4. Test session cleanup

Sessions are cleaned up when:
- WebSocket disconnects and the session is idle for 30 minutes
- The process inside the PTY exits
- The tab is closed by the user

## API Endpoints

All endpoints require authentication via `Authorization: Bearer <token>` header or `?token=` query param.

| Method | Path | Description |
|---|---|---|
| POST | `/api/session` | Create a new terminal session. Body: `{"command":"cmd.exe"}` |
| GET | `/api/sessions` | List active sessions with status |
| POST | `/api/session/destroy` | Destroy a session. Body: `{"sessionId":"..."}` |
| GET | `/api/health` | Server health: uptime, session counts, WebSocket stats |
| WS | `/ws?session=ID&token=TOKEN` | WebSocket terminal I/O |

### WebSocket Protocol

```
Client → Server:
  {"type":"input","data":"ls\r"}         — keyboard input
  {"type":"resize","cols":80,"rows":24}  — terminal resize
  {"type":"ping"}                        — keepalive

Server → Client:
  {"type":"output","data":"base64..."}   — terminal output (base64)
  {"type":"replay","data":"base64..."}   — buffered output replay on connect
  {"type":"exit","code":0}               — process exited with code
  {"type":"error","message":"..."}       — error message
  {"type":"pong"}                        — keepalive response
```

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+T | New tab (default shell) |
| Ctrl+W | Close current tab |
| Ctrl+F | Search terminal output |
| Ctrl+Shift+C | Copy selection to clipboard |
| Ctrl+Shift+V | Paste from clipboard |
| Escape | Close search bar / settings |

## Configuration

Optional config file at `%LOCALAPPDATA%\CopilotTerminalFeed\config.json`:

```json
{
  "defaultShell": "pwsh.exe",
  "port": 0,
  "fontSize": 14,
  "fontFamily": "'Cascadia Code', 'Consolas', monospace",
  "theme": "catppuccin-mocha",
  "sessionIdleTimeoutMinutes": 30,
  "allowedCommands": [
    "cmd.exe", "powershell.exe", "pwsh.exe", "wsl.exe",
    "claude", "gh"
  ]
}
```

Setting `port` to `0` (default) picks a random available port each launch.

In-browser settings (font size, cursor style, default shell, scrollback) are stored in `localStorage` and applied immediately.

## Security

- The local HTTP server binds to `localhost` only — not accessible from the network
- Every launch generates a random 128-bit auth token
- All API and WebSocket endpoints require the token (via `Authorization: Bearer <token>` header or `?token=` query param)
- Token comparison uses constant-time (`CryptographicOperations.FixedTimeEquals`) to prevent timing attacks
- Static assets (JS, CSS) are served without auth since they contain no sensitive data
- The `allowedCommands` config limits which executables can be spawned

## Troubleshooting

**Terminal page shows "Forbidden"**
- The auth token in the URL is wrong or missing. Copy the full URL from the server's console output.

**xterm.js doesn't render / blank screen**
- Check browser console for errors. xterm.js is loaded from CDN — verify internet connectivity.
- If behind a proxy, the CDN URLs may be blocked. Consider bundling xterm.js locally.

**"Command not in allowed list" error**
- The command you tried to launch isn't in `allowedCommands` in config.json. Add it or clear the list to allow all.

**WebSocket keeps reconnecting**
- The server process may have crashed. Check the server's stderr output for errors.
- On Windows, ensure the port isn't blocked by firewall rules for localhost.

**Claude / Copilot button doesn't work**
- Verify `claude` or `gh copilot` is installed and on your PATH. Try running the command in a regular terminal first.

**Widget Board doesn't show the feed**
- Ensure MSIX package is installed with Developer Mode enabled.
- Check that the COM class ID in `Package.appxmanifest` matches the one in `Program.cs`.
- Try `Win+W` to open Widget Board and look for the "Copilot Terminal" feed.

## Known Limitations

- **Windows only** — ConPTY is a Windows API. No Linux/macOS support.
- **No SSH or remote execution** — all sessions run locally on the host machine.
- **Single user** — the auth token is per-launch, no multi-user session sharing.
- **CDN dependency** — xterm.js is loaded from jsDelivr CDN; offline use requires local bundling.
- **Output buffer** — replay buffer is 64KB; very long sessions may lose earlier output on reconnect.

## Project Structure

```
CopilotTerminalFeed.sln
src/
  CopilotTerminalFeed/           # Main WinUI3 app + Feed Provider
    Program.cs                   # Entry point, starts server + registers COM
    TerminalFeedProvider.cs      # IFeedProvider implementation
    Package.appxmanifest         # MSIX manifest with COM + Widget registration
  CopilotTerminalFeed.Server/    # HTTP + WebSocket + ConPTY library
    TerminalServer.cs            # HTTP listener, routing, auth, health
    ConPtySession.cs             # ConPTY P/Invoke wrapper + output buffer
    WebSocketHandler.cs          # WS <-> PTY bridge with replay
    ServerConfig.cs              # Configuration loading
    Log.cs                       # Structured logging
web/                             # xterm.js terminal UI
  index.html                     # Accessible HTML with ARIA landmarks
  terminal.js                    # Sessions, search, settings, reconnection
  terminal.css                   # Catppuccin Mocha theme + settings panel
tests/
  CopilotTerminalFeed.Tests/     # xUnit test suite
    TerminalServerIntegrationTests.cs
    FeedProviderVerbTests.cs
    ServerConfigTests.cs
    TerminalServerTests.cs
    WebSocketProtocolTests.cs
.github/
  workflows/ci.yml               # GitHub Actions: build + test
```
