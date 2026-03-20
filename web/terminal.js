/**
 * CopilotTerminalFeed — xterm.js terminal client
 *
 * Features:
 *   - Session tabs (multiple concurrent terminals)
 *   - WebSocket reconnection with exponential backoff
 *   - Auth token from URL query param, forwarded on API/WS requests
 *   - Graceful error messages for missing commands
 */
(function () {
  'use strict';

  var BASE_URL = window.location.origin;
  var AUTH_TOKEN = new URLSearchParams(window.location.search).get('token') || '';
  var statusText = document.getElementById('status-text');
  var sessionIdEl = document.getElementById('session-id');
  var container = document.getElementById('terminal-container');
  var tabBar = document.getElementById('tab-bar');

  var sessions = {};       // { id: { terminal, fitAddon, ws, command, reconnectAttempts, reconnectTimer } }
  var activeSessionId = null;
  var tabCounter = 0;

  // ── Auth helpers ────────────────────────────────────────────────

  function authHeaders() {
    return { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + AUTH_TOKEN };
  }

  function authQueryParam() {
    return 'token=' + encodeURIComponent(AUTH_TOKEN);
  }

  // ── Terminal creation ───────────────────────────────────────────

  function createTerminal() {
    var term = new window.Terminal({
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

    var fit = new window.FitAddon.FitAddon();
    term.loadAddon(fit);
    term.loadAddon(new window.WebLinksAddon.WebLinksAddon());

    return { terminal: term, fitAddon: fit };
  }

  // ── Session management ──────────────────────────────────────────

  function startSession(command) {
    setStatus('Creating session...', '');

    fetch(BASE_URL + '/api/session', {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ command: command }),
    })
      .then(function (resp) {
        if (!resp.ok) return resp.json().then(function (d) { throw new Error(d.error || 'HTTP ' + resp.status); });
        return resp.json();
      })
      .then(function (data) {
        var id = data.sessionId;
        var pair = createTerminal();

        var session = {
          id: id,
          command: command,
          terminal: pair.terminal,
          fitAddon: pair.fitAddon,
          ws: null,
          reconnectAttempts: 0,
          reconnectTimer: null,
          wsUrl: data.wsUrl,
          exited: false,
        };

        sessions[id] = session;

        // Wire up keyboard input
        pair.terminal.onData(function (d) { sendInput(session, d); });
        pair.terminal.onResize(function (size) { sendResize(session, size.cols, size.rows); });

        addTab(id, command);
        switchToSession(id);
        connectWebSocket(session);
      })
      .catch(function (err) {
        setStatus(err.message, 'error');
        // Show error in active terminal if any
        if (activeSessionId && sessions[activeSessionId]) {
          sessions[activeSessionId].terminal.writeln('\r\n\x1b[31mError: ' + err.message + '\x1b[0m');
        }
      });
  }

  function destroySession(id) {
    var session = sessions[id];
    if (!session) return;

    if (session.reconnectTimer) clearTimeout(session.reconnectTimer);
    if (session.ws) { session.ws.onclose = null; session.ws.close(); }
    session.terminal.dispose();
    delete sessions[id];

    // Tell server to clean up
    fetch(BASE_URL + '/api/session/destroy', {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ sessionId: id }),
    }).catch(function () {});

    removeTab(id);

    // Switch to another session or show empty state
    var remaining = Object.keys(sessions);
    if (remaining.length > 0) {
      switchToSession(remaining[remaining.length - 1]);
    } else {
      activeSessionId = null;
      container.innerHTML = '';
      setStatus('No sessions', '');
      sessionIdEl.textContent = '';
    }
  }

  function switchToSession(id) {
    var session = sessions[id];
    if (!session) return;

    // Hide all terminals
    Object.keys(sessions).forEach(function (sid) {
      var el = sessions[sid].terminal.element;
      if (el) el.style.display = 'none';
    });

    activeSessionId = id;

    // If terminal not yet attached, attach it
    if (!session.terminal.element) {
      session.terminal.open(container);
    }
    session.terminal.element.style.display = '';
    session.fitAddon.fit();
    session.terminal.focus();

    // Update tab active state
    tabBar.querySelectorAll('.tab').forEach(function (t) {
      t.classList.toggle('active', t.dataset.session === id);
    });

    sessionIdEl.textContent = id;
    setStatus(session.ws && session.ws.readyState === WebSocket.OPEN ? 'Connected' : 'Disconnected',
              session.ws && session.ws.readyState === WebSocket.OPEN ? 'connected' : '');
  }

  // ── WebSocket with reconnection ─────────────────────────────────

  var MAX_RECONNECT_ATTEMPTS = 8;
  var BASE_RECONNECT_DELAY = 500; // ms

  function connectWebSocket(session) {
    if (session.exited) return;

    var url = session.wsUrl;
    session.ws = new WebSocket(url);

    session.ws.onopen = function () {
      session.reconnectAttempts = 0;
      if (activeSessionId === session.id) setStatus('Connected', 'connected');
      sendResize(session, session.terminal.cols, session.terminal.rows);
    };

    session.ws.onmessage = function (event) {
      try {
        var msg = JSON.parse(event.data);
        if (msg.type === 'output') {
          var raw = atob(msg.data);
          // Convert binary string to Uint8Array for proper UTF-8 handling
          var bytes = new Uint8Array(raw.length);
          for (var i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
          session.terminal.write(bytes);
        } else if (msg.type === 'exit') {
          session.exited = true;
          if (activeSessionId === session.id) setStatus('Process exited', '');
          session.terminal.writeln('\r\n\x1b[33m[Process exited. Press any key to close tab.]\x1b[0m');
          session.terminal.onData(function () { destroySession(session.id); });
        } else if (msg.type === 'error') {
          session.terminal.writeln('\r\n\x1b[31mServer error: ' + (msg.message || 'unknown') + '\x1b[0m');
        }
      } catch (err) {
        console.error('WebSocket message error:', err);
      }
    };

    session.ws.onclose = function () {
      if (session.exited) return;
      if (activeSessionId === session.id) setStatus('Disconnected', '');
      scheduleReconnect(session);
    };

    session.ws.onerror = function () {
      // onclose will fire after this
    };
  }

  function scheduleReconnect(session) {
    if (session.exited || session.reconnectAttempts >= MAX_RECONNECT_ATTEMPTS) {
      if (!session.exited) {
        session.terminal.writeln('\r\n\x1b[31m[Connection lost. Click to retry.]\x1b[0m');
        session.terminal.onData(function () {
          session.reconnectAttempts = 0;
          connectWebSocket(session);
        });
      }
      return;
    }

    var delay = BASE_RECONNECT_DELAY * Math.pow(2, session.reconnectAttempts);
    delay = Math.min(delay, 30000); // cap at 30s
    session.reconnectAttempts++;

    if (activeSessionId === session.id) {
      setStatus('Reconnecting (' + session.reconnectAttempts + '/' + MAX_RECONNECT_ATTEMPTS + ')...', '');
    }

    session.reconnectTimer = setTimeout(function () {
      connectWebSocket(session);
    }, delay);
  }

  // ── WebSocket messaging ─────────────────────────────────────────

  function sendInput(session, data) {
    if (session.ws && session.ws.readyState === WebSocket.OPEN) {
      session.ws.send(JSON.stringify({ type: 'input', data: data }));
    }
  }

  function sendResize(session, cols, rows) {
    if (session.ws && session.ws.readyState === WebSocket.OPEN) {
      session.ws.send(JSON.stringify({ type: 'resize', cols: cols, rows: rows }));
    }
  }

  // ── Tab bar ─────────────────────────────────────────────────────

  function addTab(id, command) {
    tabCounter++;
    var label = command.split(/[\\/]/).pop().split('.')[0]; // "cmd.exe" -> "cmd"
    if (label === 'gh') label = 'copilot';

    var tab = document.createElement('div');
    tab.className = 'tab';
    tab.dataset.session = id;
    tab.innerHTML = '<span class="tab-label">' + escapeHtml(label) + '</span>' +
                    '<span class="tab-close" title="Close">&times;</span>';

    tab.querySelector('.tab-label').addEventListener('click', function () {
      switchToSession(id);
    });

    tab.querySelector('.tab-close').addEventListener('click', function (e) {
      e.stopPropagation();
      destroySession(id);
    });

    // Insert before the "+" button
    var addBtn = tabBar.querySelector('.tab-add');
    tabBar.insertBefore(tab, addBtn);
  }

  function removeTab(id) {
    var tab = tabBar.querySelector('.tab[data-session="' + id + '"]');
    if (tab) tab.remove();
  }

  function escapeHtml(s) {
    var d = document.createElement('div');
    d.textContent = s;
    return d.innerHTML;
  }

  // ── UI helpers ──────────────────────────────────────────────────

  function setStatus(text, className) {
    statusText.textContent = text;
    statusText.className = className || '';
  }

  // ── Window resize ───────────────────────────────────────────────

  window.addEventListener('resize', function () {
    if (activeSessionId && sessions[activeSessionId]) {
      sessions[activeSessionId].fitAddon.fit();
    }
  });

  // ── Toolbar command buttons ─────────────────────────────────────

  document.querySelectorAll('.cmd-btn').forEach(function (btn) {
    btn.addEventListener('click', function () {
      startSession(btn.dataset.command);
    });
  });

  // ── Tab bar "+" button ──────────────────────────────────────────

  document.querySelector('.tab-add').addEventListener('click', function () {
    startSession('cmd.exe');
  });

  // ── Initialize ──────────────────────────────────────────────────

  startSession('cmd.exe');
})();
