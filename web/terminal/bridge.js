// Agent Workspace Terminal — xterm.js host
//
// One xterm.js instance per pane lives inside a single WebView2. Communication with the .NET
// host is via WebView2.postMessage in both directions. Messages are JSON; output bytes are
// base64-encoded (WebView2 string messages only).
//
// Inbound (host -> JS):
//   { type: "init",       paneId, theme? }
//   { type: "output",     paneId, b64 }
//   { type: "exit",       paneId, code }
//   { type: "status",     text }
//   { type: "clear",      paneId? }       // xterm.clear()
//   { type: "fontSize",   delta? | size?} // adjust active pane font size; refit
//   { type: "focusTerm" }                 // restore focus to terminal (after palette close)
//
// Outbound (JS -> host):
//   { type: "ready" }
//   { type: "input",        paneId, b64 }
//   { type: "resize",       paneId, cols, rows }
//   { type: "paletteToggle" }             // user pressed Ctrl+Shift+P
//   { type: "log",          level, message }

(() => {
  "use strict";

  const status = document.getElementById("status");
  const paneEl = document.getElementById("pane");

  /** @type {Map<string, {term: any, fit: any, fontSize: number}>} */
  const panes = new Map();

  // Active pane id; for MVP-1 there is only one. MVP-2 introduces a layout tree.
  let activePane = null;

  const post = (msg) => {
    try {
      window.chrome?.webview?.postMessage(JSON.stringify(msg));
    } catch (err) {
      console.error("postMessage failed", err);
    }
  };

  const setStatus = (text) => {
    status.textContent = text;
  };

  const b64ToBytes = (b64) => {
    const bin = atob(b64);
    const len = bin.length;
    const out = new Uint8Array(len);
    for (let i = 0; i < len; i++) out[i] = bin.charCodeAt(i);
    return out;
  };

  const bytesToB64 = (bytes) => {
    let bin = "";
    const len = bytes.length;
    for (let i = 0; i < len; i += 0x8000) {
      bin += String.fromCharCode.apply(
        null,
        bytes.subarray(i, Math.min(i + 0x8000, len))
      );
    }
    return btoa(bin);
  };

  const stringToBytes = (s) => new TextEncoder().encode(s);

  const createPane = (paneId, theme) => {
    if (panes.has(paneId)) return panes.get(paneId);

    const fontSize = 13;
    const term = new Terminal({
      cursorBlink: true,
      cursorStyle: "block",
      fontFamily:
        '"Cascadia Code", "Cascadia Mono", Consolas, "Lucida Console", monospace',
      fontSize,
      theme: theme ?? {
        background: "#0b0e14",
        foreground: "#cbd2dc",
        cursor: "#79c0ff",
        selectionBackground: "#264f78",
      },
      allowProposedApi: true,
      convertEol: false,
      windowsPty: { backend: "conpty" },
      scrollback: 5000,
    });

    const fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    term.loadAddon(new WebLinksAddon.WebLinksAddon());

    // Intercept Ctrl+Shift+P before xterm sees it; relay to host. Returning false from this
    // handler tells xterm to skip its default processing.
    term.attachCustomKeyEventHandler((e) => {
      if (e.type !== "keydown") return true;
      if (e.ctrlKey && e.shiftKey && (e.key === "P" || e.key === "p")) {
        e.preventDefault?.();
        post({ type: "paletteToggle" });
        return false;
      }
      return true;
    });

    term.open(paneEl);
    fit.fit();
    term.focus();

    term.onData((s) => {
      const bytes = stringToBytes(s);
      post({ type: "input", paneId, b64: bytesToB64(bytes) });
    });

    term.onBinary((s) => {
      const bytes = new Uint8Array(s.length);
      for (let i = 0; i < s.length; i++) bytes[i] = s.charCodeAt(i) & 0xff;
      post({ type: "input", paneId, b64: bytesToB64(bytes) });
    });

    term.onResize(({ cols, rows }) => {
      post({ type: "resize", paneId, cols, rows });
    });

    const entry = { term, fit, fontSize };
    panes.set(paneId, entry);
    activePane = paneId;
    return entry;
  };

  const adjustFontSize = (entry, delta, absolute) => {
    if (!entry) return;
    let next;
    if (typeof absolute === "number") {
      next = absolute;
    } else {
      next = entry.fontSize + (delta ?? 0);
    }
    next = Math.max(8, Math.min(36, next | 0));
    if (next === entry.fontSize) return;
    entry.fontSize = next;
    entry.term.options.fontSize = next;
    // Refit so the cell grid lines up with the new font metrics.
    try { entry.fit.fit(); } catch (e) { /* no-op while detached */ }
    setStatus(`font ${next}px`);
  };

  const clearPane = (paneId) => {
    const id = paneId ?? activePane;
    if (!id) return;
    const entry = panes.get(id);
    if (!entry) return;
    entry.term.clear();
  };

  const focusActiveTerm = () => {
    if (!activePane) return;
    const entry = panes.get(activePane);
    if (entry) entry.term.focus();
  };

  const handleMessage = (raw) => {
    let msg;
    try {
      msg = typeof raw === "string" ? JSON.parse(raw) : raw;
    } catch {
      return;
    }
    if (!msg || typeof msg !== "object") return;

    switch (msg.type) {
      case "init": {
        const entry = createPane(msg.paneId, msg.theme);
        const { cols, rows } = entry.term;
        post({ type: "resize", paneId: msg.paneId, cols, rows });
        setStatus(`pane ${msg.paneId.slice(0, 6)}…  ${cols}×${rows}`);
        break;
      }
      case "output": {
        const entry = panes.get(msg.paneId);
        if (!entry) return;
        entry.term.write(b64ToBytes(msg.b64));
        break;
      }
      case "exit": {
        const entry = panes.get(msg.paneId);
        if (!entry) return;
        entry.term.write(
          `\r\n\x1b[2m[process exited with code ${msg.code}]\x1b[0m\r\n`
        );
        setStatus(`pane ${msg.paneId.slice(0, 6)}… exited (${msg.code})`);
        break;
      }
      case "status": {
        setStatus(msg.text);
        break;
      }
      case "clear": {
        clearPane(msg.paneId);
        break;
      }
      case "fontSize": {
        const entry = panes.get(msg.paneId ?? activePane);
        adjustFontSize(entry, msg.delta, msg.size);
        break;
      }
      case "focusTerm": {
        focusActiveTerm();
        break;
      }
    }
  };

  if (window.chrome?.webview) {
    window.chrome.webview.addEventListener("message", (e) => handleMessage(e.data));
  } else {
    setStatus("Not running inside WebView2.");
  }

  window.addEventListener("resize", () => {
    if (!activePane) return;
    const entry = panes.get(activePane);
    if (entry) entry.fit.fit();
  });

  post({ type: "ready" });
  setStatus("Ready, waiting for host…");
})();
