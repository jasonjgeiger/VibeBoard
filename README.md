# CopilotTerminalFeed

A Windows App SDK feed provider that embeds an interactive terminal in the Windows Widget Board. Targets **Claude Code CLI** and **GitHub Copilot** as primary commands, but works as a general-purpose PTY terminal.

## Architecture

Three components, MSIX-packaged:

| Component | Description |
|---|---|
| **CopilotTerminalFeed** | WinUI3 app registering as a Feed Provider via `IFeedProvider` COM interface. Sends an Adaptive Card with a `ContentUri` pointing to the local server. |
| **CopilotTerminalFeed.Server** | Background HTTP + WebSocket server. Manages ConPTY sessions and relays terminal I/O. Authenticated with a per-launch token. |
| **web/** | xterm.js terminal UI served as a local webpage. Supports multiple session tabs, reconnection, and Catppuccin Mocha theme. |

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
- A tab bar with a cmd.exe session auto-started
- A working terminal where you can type commands

Try:
- Click **Claude** to open a `claude` session (requires Claude Code installed)
- Click **Copilot** to open a `gh copilot` session (requires GitHub CLI + Copilot extension)
- Click **+** in the tab bar to open additional shell tabs
- Close a tab by clicking the **x** on it

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
2. Start a shell session
3. Kill and restart the server process
4. The browser tab should automatically reconnect (status bar shows "Reconnecting...")

### 4. Test session cleanup

Sessions are cleaned up when:
- WebSocket disconnects and the session is idle for 30 minutes
- The process inside the PTY exits
- The tab is closed by the user

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

## Security

- The local HTTP server binds to `localhost` only — not accessible from the network
- Every launch generates a random 128-bit auth token
- All API and WebSocket endpoints require the token (via `Authorization: Bearer <token>` header or `?token=` query param)
- Static assets (JS, CSS) are served without auth since they contain no sensitive data
- The `allowedCommands` config limits which executables can be spawned

## Project Structure

```
CopilotTerminalFeed.sln
src/
  CopilotTerminalFeed/           # Main WinUI3 app + Feed Provider
    Program.cs                   # Entry point, starts server + registers COM
    TerminalFeedProvider.cs      # IFeedProvider implementation
    Package.appxmanifest         # MSIX manifest with COM + Widget registration
  CopilotTerminalFeed.Server/    # HTTP + WebSocket + ConPTY library
    TerminalServer.cs            # HTTP listener, routing, auth
    ConPtySession.cs             # ConPTY P/Invoke wrapper
    WebSocketHandler.cs          # WS <-> PTY bridge
    ServerConfig.cs              # Configuration loading
web/                             # xterm.js terminal UI
  index.html
  terminal.js                    # Session tabs, reconnection, auth
  terminal.css                   # Catppuccin Mocha theme
tests/
  CopilotTerminalFeed.Tests/     # Unit tests
```
