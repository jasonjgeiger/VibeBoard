/**
 * CopilotTerminalFeed — xterm.js terminal client
 *
 * Connects to the local TerminalServer via WebSocket and bridges
 * keyboard input / terminal output through a ConPTY session.
 */
(function () {
  'use strict';

  const BASE_URL = window.location.origin;
  const statusText = document.getElementById('status-text');
  const sessionIdEl = document.getElementById('session-id');
  const container = document.getElementById('terminal-container');

  let terminal = null;
  let fitAddon = null;
  let ws = null;
  let currentSessionId = null;

  // ── Terminal setup ──────────────────────────────────────────────

  function createTerminal() {
    if (terminal) {
      terminal.dispose();
    }

    terminal = new window.Terminal({
      cursorBlink: true,
      cursorStyle: 'bar',
      fontSize: 14,
      fontFamily: "'Cascadia Code', 'Consolas', 'Courier New', monospace",
      theme: {
        background: '#1e1e2e',
        foreground: '#cdd6f4',
        cursor: '#f5e0dc',
        selectionBackground: '#45475a',
        black: '#45475a',
        red: '#f38ba8',
        green: '#a6e3a1',
        yellow: '#f9e2af',
        blue: '#89b4fa',
        magenta: '#cba6f7',
        cyan: '#94e2d5',
        white: '#bac2de',
        brightBlack: '#585b70',
        brightRed: '#f38ba8',
        brightGreen: '#a6e3a1',
        brightYellow: '#f9e2af',
        brightBlue: '#89b4fa',
        brightMagenta: '#cba6f7',
        brightCyan: '#94e2d5',
        brightWhite: '#a6adc8',
      },
      allowProposedApi: true,
    });

    fitAddon = new window.FitAddon.FitAddon();
    terminal.loadAddon(fitAddon);
    terminal.loadAddon(new window.WebLinksAddon.WebLinksAddon());

    terminal.open(container);
    fitAddon.fit();

    // Forward keyboard input to the server
    terminal.onData(function (data) {
      sendInput(data);
    });

    // Handle resize
    terminal.onResize(function (size) {
      sendResize(size.cols, size.rows);
    });

    window.addEventListener('resize', function () {
      if (fitAddon) fitAddon.fit();
    });

    return terminal;
  }

  // ── Session management ──────────────────────────────────────────

  async function startSession(command) {
    setStatus('Connecting...', '');

    // Close existing WebSocket
    if (ws) {
      ws.close();
      ws = null;
    }

    try {
      const resp = await fetch(BASE_URL + '/api/session', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ command: command }),
      });

      if (!resp.ok) {
        throw new Error('Failed to create session: ' + resp.status);
      }

      const data = await resp.json();
      currentSessionId = data.sessionId;
      sessionIdEl.textContent = currentSessionId;

      // Connect WebSocket
      connectWebSocket(data.wsUrl);
    } catch (err) {
      setStatus(err.message, 'error');
      terminal.writeln('\r\n\x1b[31mError: ' + err.message + '\x1b[0m');
    }
  }

  function connectWebSocket(url) {
    ws = new WebSocket(url);

    ws.onopen = function () {
      setStatus('Connected', 'connected');
      // Send initial size
      if (terminal) {
        sendResize(terminal.cols, terminal.rows);
      }
    };

    ws.onmessage = function (event) {
      try {
        var msg = JSON.parse(event.data);

        if (msg.type === 'output') {
          // Decode base64 output and write to terminal
          var bytes = atob(msg.data);
          terminal.write(bytes);
        } else if (msg.type === 'exit') {
          setStatus('Process exited', '');
          terminal.writeln('\r\n\x1b[33m[Process exited]\x1b[0m');
        }
      } catch (err) {
        console.error('WebSocket message error:', err);
      }
    };

    ws.onclose = function () {
      setStatus('Disconnected', '');
    };

    ws.onerror = function () {
      setStatus('Connection error', 'error');
    };
  }

  // ── WebSocket messaging ─────────────────────────────────────────

  function sendInput(data) {
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: 'input', data: data }));
    }
  }

  function sendResize(cols, rows) {
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: 'resize', cols: cols, rows: rows }));
    }
  }

  // ── UI helpers ──────────────────────────────────────────────────

  function setStatus(text, className) {
    statusText.textContent = text;
    statusText.className = className || '';
  }

  // ── Button handlers ─────────────────────────────────────────────

  document.querySelectorAll('.cmd-btn').forEach(function (btn) {
    btn.addEventListener('click', function () {
      // Update active state
      document.querySelectorAll('.cmd-btn').forEach(function (b) {
        b.classList.remove('active');
      });
      btn.classList.add('active');

      // Create new terminal and session
      createTerminal();
      startSession(btn.dataset.command);
    });
  });

  // ── Initialize ──────────────────────────────────────────────────

  createTerminal();
  terminal.writeln('Copilot Terminal Feed');
  terminal.writeln('Select a command above or click Shell to start.\r\n');

  // Auto-start a shell session
  startSession('cmd.exe');
})();
