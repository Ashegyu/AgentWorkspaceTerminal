// Agent Workspace Terminal — xterm.js host
//
// One xterm.js instance per pane lives inside a single WebView2. Communication with the .NET
// host is via WebView2.postMessage in both directions. Messages are JSON; output bytes are
// base64-encoded (WebView2 string messages only).
//
// Inbound (host -> JS):
//   { type: "init",   paneId, theme? }
//   { type: "output", paneId, b64 }
//   { type: "exit",   paneId, code }
//   { type: "status", text }
//
// Outbound (JS -> host):
//   { type: "ready" }
//   { type: "input",  paneId, b64 }
//   { type: "resize", paneId, cols, rows }
//   { type: "log",    level, message }

(() => {
  "use strict";

  const status = document.getElementById("status");
  const paneEl = document.getElementById("pane");

  /** @type {Map<string, {term: any, fit: any}>} */
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
    // Avoid String.fromCharCode.apply on huge arrays — chunked is safer.
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

    const term = new Terminal({
      cursorBlink: true,
      cursorStyle: "block",
      fontFamily:
        '"Cascadia Code", "Cascadia Mono", Consolas, "Lucida Console", monospace',
      fontSize: 13,
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

    term.open(paneEl);
    fit.fit();
    term.focus();

    // Forward keystrokes as raw bytes — xterm.js gives us a string of code points which the
    // user's keymap has already converted; we re-encode as UTF-8 because ConPTY consumes bytes.
    term.onData((s) => {
      const bytes = stringToBytes(s);
      post({ type: "input", paneId, b64: bytesToB64(bytes) });
    });

    // Same channel for binary (e.g. paste) — kept for future.
    term.onBinary((s) => {
      const bytes = new Uint8Array(s.length);
      for (let i = 0; i < s.length; i++) bytes[i] = s.charCodeAt(i) & 0xff;
      post({ type: "input", paneId, b64: bytesToB64(bytes) });
    });

    term.onResize(({ cols, rows }) => {
      post({ type: "resize", paneId, cols, rows });
    });

    const entry = { term, fit };
    panes.set(paneId, entry);
    activePane = paneId;
    return entry;
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
        // Push initial size to host so it can drive ResizeAsync once.
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
    }
  };

  // Receive messages from .NET host. WebView2 delivers via window.chrome.webview.message
  // events; the payload is a string (we serialise as JSON above).
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

  // Tell host we're alive so it can send 'init'.
  post({ type: "ready" });
  setStatus("Ready, waiting for host…");
})();
