/**
 * CopilotTerminalFeed — xterm.js terminal client
 *
 * Features:
 *   - Session tabs (multiple concurrent terminals)
 *   - WebSocket reconnection with exponential backoff
 *   - Auth token from URL query param, forwarded on API/WS requests
 *   - Clipboard integration (Ctrl+Shift+C/V)
 *   - In-terminal search (Ctrl+F) via SearchAddon
 *   - Settings panel (font size, default shell, scrollback, cursor)
 *   - Session persistence across page reloads
 */
(function () {
  'use strict';

  var BASE_URL = window.location.origin;
  var AUTH_TOKEN = new URLSearchParams(window.location.search).get('token') || '';
  var statusText = document.getElementById('status-text');
  var sessionIdEl = document.getElementById('session-id');
  var container = document.getElementById('terminal-container');
  var tabBar = document.getElementById('tab-bar');

  var sessions = {};
  var activeSessionId = null;
  var tabCounter = 0;

  // ── Local settings (persisted to localStorage) ──────────────────

  var SETTINGS_KEY = 'copilot-terminal-settings';
  var DEFAULT_SETTINGS = {
    fontSize: 14,
    defaultShell: 'cmd.exe',
    scrollback: 5000,
    cursorStyle: 'bar',
  };

  function loadSettings() {
    try {
      var stored = localStorage.getItem(SETTINGS_KEY);
      if (stored) {
        var parsed = JSON.parse(stored);
        return Object.assign({}, DEFAULT_SETTINGS, parsed);
      }
    } catch (e) {}
    return Object.assign({}, DEFAULT_SETTINGS);
  }

  function saveSettings(s) {
    try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(s)); } catch (e) {}
  }

  var settings = loadSettings();

  // ── Session persistence (remember across reloads) ───────────────

  var SESSIONS_KEY = 'copilot-terminal-sessions';

  function persistSessionList() {
    var list = Object.keys(sessions).map(function (id) {
      var s = sessions[id];
      return { id: id, command: s.command, wsUrl: s.wsUrl };
    });
    try { localStorage.setItem(SESSIONS_KEY, JSON.stringify(list)); } catch (e) {}
  }

  function loadPersistedSessions() {
    try {
      var stored = localStorage.getItem(SESSIONS_KEY);
      if (stored) return JSON.parse(stored);
    } catch (e) {}
    return [];
  }

  function clearPersistedSessions() {
    try { localStorage.removeItem(SESSIONS_KEY); } catch (e) {}
  }

  // ── Auth helpers ────────────────────────────────────────────────

  function authHeaders() {
    return { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + AUTH_TOKEN };
  }

  // ── Terminal creation ───────────────────────────────────────────

  function createTerminal() {
    var term = new window.Terminal({
      cursorBlink: true,
      cursorStyle: settings.cursorStyle,
      fontSize: settings.fontSize,
      fontFamily: "'Cascadia Code', 'Consolas', 'Courier New', monospace",
      scrollback: settings.scrollback,
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

    var search = new window.SearchAddon.SearchAddon();
    term.loadAddon(search);

    // Clipboard: Ctrl+Shift+C to copy, Ctrl+Shift+V to paste
    term.attachCustomKeyEventHandler(function (ev) {
      if (ev.ctrlKey && ev.shiftKey && ev.type === 'keydown') {
        if (ev.key === 'C') {
          var sel = term.getSelection();
          if (sel) navigator.clipboard.writeText(sel).catch(function () {});
          return false;
        }
        if (ev.key === 'V') {
          navigator.clipboard.readText().then(function (text) {
            if (text && activeSessionId) sendInput(sessions[activeSessionId], text);
          }).catch(function () {});
          return false;
        }
      }
      // Ctrl+F opens search bar (prevent terminal from receiving it)
      if (ev.ctrlKey && !ev.shiftKey && ev.key === 'f' && ev.type === 'keydown') {
        openSearchBar();
        return false;
      }
      return true;
    });

    return { terminal: term, fitAddon: fit, searchAddon: search };
  }

  // ── Search bar ──────────────────────────────────────────────────

  var searchBar = document.getElementById('search-bar');
  var searchInput = document.getElementById('search-input');

  function openSearchBar() {
    searchBar.classList.remove('hidden');
    searchInput.focus();
    searchInput.select();
  }

  function closeSearchBar() {
    searchBar.classList.add('hidden');
    // Clear search decoration
    if (activeSessionId && sessions[activeSessionId]) {
      sessions[activeSessionId].searchAddon.clearDecorations();
      sessions[activeSessionId].terminal.focus();
    }
  }

  function doSearch(direction) {
    var query = searchInput.value;
    if (!query || !activeSessionId || !sessions[activeSessionId]) return;
    var addon = sessions[activeSessionId].searchAddon;
    if (direction === 'prev') {
      addon.findPrevious(query);
    } else {
      addon.findNext(query);
    }
  }

  searchInput.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') { closeSearchBar(); e.preventDefault(); }
    else if (e.key === 'Enter' && e.shiftKey) { doSearch('prev'); e.preventDefault(); }
    else if (e.key === 'Enter') { doSearch('next'); e.preventDefault(); }
  });

  document.getElementById('search-next').addEventListener('click', function () { doSearch('next'); });
  document.getElementById('search-prev').addEventListener('click', function () { doSearch('prev'); });
  document.getElementById('search-close').addEventListener('click', closeSearchBar);

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
        attachSession(data.sessionId, command, data.wsUrl, true);
      })
      .catch(function (err) {
        setStatus(err.message, 'error');
        if (activeSessionId && sessions[activeSessionId]) {
          sessions[activeSessionId].terminal.writeln('\r\n\x1b[31mError: ' + err.message + '\x1b[0m');
        }
      });
  }

  function attachSession(id, command, wsUrl, isNew) {
    var pair = createTerminal();

    var session = {
      id: id,
      command: command,
      terminal: pair.terminal,
      fitAddon: pair.fitAddon,
      searchAddon: pair.searchAddon,
      ws: null,
      reconnectAttempts: 0,
      reconnectTimer: null,
      wsUrl: wsUrl,
      exited: false,
    };

    sessions[id] = session;

    pair.terminal.onData(function (d) { sendInput(session, d); });
    pair.terminal.onResize(function (size) { sendResize(session, size.cols, size.rows); });

    addTab(id, command);
    switchToSession(id);
    connectWebSocket(session);

    persistSessionList();
  }

  function destroySession(id) {
    var session = sessions[id];
    if (!session) return;

    if (session.reconnectTimer) clearTimeout(session.reconnectTimer);
    if (session.ws) { session.ws.onclose = null; session.ws.close(); }
    session.terminal.dispose();
    delete sessions[id];

    fetch(BASE_URL + '/api/session/destroy', {
      method: 'POST',
      headers: authHeaders(),
      body: JSON.stringify({ sessionId: id }),
    }).catch(function () {});

    removeTab(id);
    persistSessionList();

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

    Object.keys(sessions).forEach(function (sid) {
      var el = sessions[sid].terminal.element;
      if (el) el.style.display = 'none';
    });

    activeSessionId = id;

    if (!session.terminal.element) {
      session.terminal.open(container);
    }
    session.terminal.element.style.display = '';
    session.fitAddon.fit();
    session.terminal.focus();

    tabBar.querySelectorAll('.tab').forEach(function (t) {
      t.classList.toggle('active', t.dataset.session === id);
    });

    sessionIdEl.textContent = id;
    setStatus(session.ws && session.ws.readyState === WebSocket.OPEN ? 'Connected' : 'Disconnected',
              session.ws && session.ws.readyState === WebSocket.OPEN ? 'connected' : '');
  }

  // ── WebSocket with reconnection ─────────────────────────────────

  var MAX_RECONNECT_ATTEMPTS = 8;
  var BASE_RECONNECT_DELAY = 500;

  function connectWebSocket(session) {
    if (session.exited) return;

    session.ws = new WebSocket(session.wsUrl);

    session.ws.onopen = function () {
      session.reconnectAttempts = 0;
      if (activeSessionId === session.id) setStatus('Connected', 'connected');
      sendResize(session, session.terminal.cols, session.terminal.rows);
    };

    session.ws.onmessage = function (event) {
      try {
        var msg = JSON.parse(event.data);
        if (msg.type === 'output' || msg.type === 'replay') {
          var raw = atob(msg.data);
          var bytes = new Uint8Array(raw.length);
          for (var i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
          session.terminal.write(bytes);
        } else if (msg.type === 'exit') {
          session.exited = true;
          var codeStr = typeof msg.code === 'number' ? ' (code ' + msg.code + ')' : '';
          if (activeSessionId === session.id) setStatus('Process exited' + codeStr, '');
          session.terminal.writeln('\r\n\x1b[33m[Process exited' + codeStr + '. Press any key to close tab.]\x1b[0m');
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

    session.ws.onerror = function () {};
  }

  function scheduleReconnect(session) {
    if (session.exited || session.reconnectAttempts >= MAX_RECONNECT_ATTEMPTS) {
      if (!session.exited) {
        session.terminal.writeln('\r\n\x1b[31m[Connection lost. Press any key to retry.]\x1b[0m');
        session.terminal.onData(function () {
          session.reconnectAttempts = 0;
          connectWebSocket(session);
        });
      }
      return;
    }

    var delay = BASE_RECONNECT_DELAY * Math.pow(2, session.reconnectAttempts);
    delay = Math.min(delay, 30000);
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
    var label = command.split(/[\\/]/).pop().split('.')[0];
    if (label === 'gh') label = 'copilot';

    var tab = document.createElement('div');
    tab.className = 'tab';
    tab.dataset.session = id;
    tab.setAttribute('role', 'tab');
    tab.setAttribute('aria-label', label + ' terminal session');
    tab.setAttribute('tabindex', '0');
    tab.innerHTML = '<span class="tab-label">' + escapeHtml(label) + '</span>' +
                    '<span class="tab-close" title="Close" aria-label="Close tab">&times;</span>';

    tab.querySelector('.tab-label').addEventListener('click', function () {
      switchToSession(id);
    });

    tab.querySelector('.tab-close').addEventListener('click', function (e) {
      e.stopPropagation();
      destroySession(id);
    });

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

  // ── Settings panel ──────────────────────────────────────────────

  var settingsOverlay = document.getElementById('settings-overlay');

  function openSettings() {
    document.getElementById('setting-fontsize').value = settings.fontSize;
    document.getElementById('setting-shell').value = settings.defaultShell;
    document.getElementById('setting-scrollback').value = settings.scrollback;
    document.getElementById('setting-cursor').value = settings.cursorStyle;
    settingsOverlay.classList.remove('hidden');
    // Focus the first input for keyboard accessibility
    document.getElementById('setting-fontsize').focus();
  }

  function closeSettings() {
    settingsOverlay.classList.add('hidden');
    if (activeSessionId && sessions[activeSessionId]) {
      sessions[activeSessionId].terminal.focus();
    }
  }

  // Focus trapping inside the settings modal
  settingsOverlay.addEventListener('keydown', function (e) {
    if (e.key !== 'Tab') return;
    var panel = settingsOverlay.querySelector('.settings-panel');
    var focusable = panel.querySelectorAll('input, select, button, [tabindex]:not([tabindex="-1"])');
    if (focusable.length === 0) return;
    var first = focusable[0];
    var last = focusable[focusable.length - 1];
    if (e.shiftKey) {
      if (document.activeElement === first) { e.preventDefault(); last.focus(); }
    } else {
      if (document.activeElement === last) { e.preventDefault(); first.focus(); }
    }
  });

  function applySettings() {
    settings.fontSize = parseInt(document.getElementById('setting-fontsize').value, 10) || 14;
    settings.defaultShell = document.getElementById('setting-shell').value;
    settings.scrollback = parseInt(document.getElementById('setting-scrollback').value, 10) || 5000;
    settings.cursorStyle = document.getElementById('setting-cursor').value;
    saveSettings(settings);

    // Apply to all existing terminals
    Object.keys(sessions).forEach(function (id) {
      var term = sessions[id].terminal;
      term.options.fontSize = settings.fontSize;
      term.options.cursorStyle = settings.cursorStyle;
      sessions[id].fitAddon.fit();
    });

    closeSettings();
  }

  document.getElementById('btn-settings').addEventListener('click', openSettings);
  document.getElementById('settings-close').addEventListener('click', closeSettings);
  document.getElementById('settings-save').addEventListener('click', applySettings);

  settingsOverlay.addEventListener('click', function (e) {
    if (e.target === settingsOverlay) closeSettings();
  });

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

  document.querySelectorAll('.cmd-btn[data-command]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      startSession(btn.dataset.command);
    });
  });

  // ── Tab bar "+" button ──────────────────────────────────────────

  document.querySelector('.tab-add').addEventListener('click', function () {
    startSession(settings.defaultShell);
  });

  // ── Keyboard shortcuts ──────────────────────────────────────────

  document.addEventListener('keydown', function (e) {
    if (e.ctrlKey && e.key === 't') {
      e.preventDefault();
      startSession(settings.defaultShell);
    }
    if (e.ctrlKey && e.key === 'w') {
      e.preventDefault();
      if (activeSessionId) destroySession(activeSessionId);
    }
    if (e.key === 'Escape') {
      if (!searchBar.classList.contains('hidden')) closeSearchBar();
      if (!settingsOverlay.classList.contains('hidden')) closeSettings();
    }
  });

  // ── Initialize ──────────────────────────────────────────────────

  // Try to reconnect to previously active sessions
  var persisted = loadPersistedSessions();
  if (persisted.length > 0) {
    // Verify sessions still exist on the server before reconnecting
    fetch(BASE_URL + '/api/sessions', { headers: authHeaders() })
      .then(function (resp) { return resp.json(); })
      .then(function (serverSessions) {
        var serverIds = {};
        serverSessions.forEach(function (s) { serverIds[s.id] = s; });

        var reconnected = false;
        persisted.forEach(function (p) {
          if (serverIds[p.id] && serverIds[p.id].running) {
            attachSession(p.id, p.command, p.wsUrl, false);
            reconnected = true;
          }
        });

        if (!reconnected) {
          clearPersistedSessions();
          startSession(settings.defaultShell);
        }
      })
      .catch(function () {
        clearPersistedSessions();
        startSession(settings.defaultShell);
      });
  } else {
    startSession(settings.defaultShell);
  }
})();
