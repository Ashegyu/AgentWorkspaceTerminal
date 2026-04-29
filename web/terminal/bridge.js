// Agent Workspace Terminal — xterm.js host (multi-pane).
//
// One xterm.js instance per pane lives inside a single WebView2. Communication with the .NET
// host is via WebView2.postMessage in both directions. Messages are JSON; output bytes are
// base64-encoded (WebView2 string messages only).
//
// Inbound (host -> JS):
//   { type: "layout",     tree, focused }       // tree = LayoutNode JSON (see below)
//   { type: "openPane",   paneId, theme? }      // create xterm for paneId, dims will follow
//   { type: "closePane",  paneId }              // dispose xterm for paneId
//   { type: "output",     paneId, b64 }
//   { type: "exit",       paneId, code }
//   { type: "status",     text }
//   { type: "clear",      paneId? }
//   { type: "fontSize",   delta? | size? }
//   { type: "focusTerm" }
//
// Outbound (JS -> host):
//   { type: "ready" }
//   { type: "input",        paneId, b64 }
//   { type: "resize",       paneId, cols, rows }
//   { type: "focusPane",    paneId }            // user clicked a pane to take focus
//   { type: "paletteToggle" }
//
// Tree JSON shape (matches AgentWorkspace.Abstractions.Layout):
//   PaneNode  = { kind: "pane",  id, paneId }
//   SplitNode = { kind: "split", id, direction: "horizontal"|"vertical", ratio, a, b }

(() => {
  "use strict";

  const status = document.getElementById("status");
  const paneRoot = document.getElementById("pane-root");

  /** @type {Map<string, {term: any, fit: any, fontSize: number, el: HTMLElement}>} */
  const panes = new Map();

  let activePane = null;
  /** Last received tree, kept so window resize can re-flow without an extra round trip. */
  let lastTree = null;

  const post = (msg) => {
    try { window.chrome?.webview?.postMessage(JSON.stringify(msg)); }
    catch (err) { console.error("postMessage failed", err); }
  };

  const setStatus = (text) => { status.textContent = text; };

  const b64ToBytes = (b64) => {
    const bin = atob(b64);
    const len = bin.length;
    const out = new Uint8Array(len);
    for (let i = 0; i < len; i++) out[i] = bin.charCodeAt(i);
    return out;
  };

  const bytesToB64 = (bytes) => {
    let bin = "";
    for (let i = 0; i < bytes.length; i += 0x8000) {
      bin += String.fromCharCode.apply(null, bytes.subarray(i, Math.min(i + 0x8000, bytes.length)));
    }
    return btoa(bin);
  };

  const stringToBytes = (s) => new TextEncoder().encode(s);

  const createPane = (paneId, theme) => {
    if (panes.has(paneId)) return panes.get(paneId);

    const el = document.createElement("div");
    el.className = "pane";
    el.dataset.paneId = paneId;
    el.addEventListener("mousedown", () => {
      if (activePane !== paneId) post({ type: "focusPane", paneId });
    });
    paneRoot.appendChild(el);

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

    term.attachCustomKeyEventHandler((e) => {
      if (e.type !== "keydown") return true;
      if (e.ctrlKey && e.shiftKey && (e.key === "P" || e.key === "p")) {
        e.preventDefault?.();
        post({ type: "paletteToggle" });
        return false;
      }
      return true;
    });

    term.open(el);

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

    const entry = { term, fit, fontSize, el };
    panes.set(paneId, entry);
    return entry;
  };

  const closePane = (paneId) => {
    const entry = panes.get(paneId);
    if (!entry) return;
    try { entry.term.dispose(); } catch { /* ignore */ }
    try { entry.el.remove(); } catch { /* ignore */ }
    panes.delete(paneId);
  };

  const adjustFontSize = (entry, delta, absolute) => {
    if (!entry) return;
    const next = Math.max(8, Math.min(36, (typeof absolute === "number" ? absolute : entry.fontSize + (delta ?? 0)) | 0));
    if (next === entry.fontSize) return;
    entry.fontSize = next;
    entry.term.options.fontSize = next;
    try { entry.fit.fit(); } catch { /* no-op while detached */ }
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

  /**
   * Lays the panes out absolutely-positioned according to the layout tree. Each rectangle is
   * given as percentages so window resize re-flows for free via CSS. After positioning, fit()
   * is called so the cell grid matches the new pixel rectangle.
   */
  const applyLayout = (tree, focused) => {
    if (!tree) return;
    lastTree = tree;
    activePane = focused ?? activePane;

    const placements = [];
    const walk = (node, x, y, w, h) => {
      if (node.kind === "pane") {
        placements.push({ paneId: node.paneId, x, y, w, h });
        return;
      }
      const r = Math.max(0.05, Math.min(0.95, node.ratio ?? 0.5));
      if (node.direction === "horizontal") {
        walk(node.a, x, y, w * r, h);
        walk(node.b, x + w * r, y, w * (1 - r), h);
      } else {
        walk(node.a, x, y, w, h * r);
        walk(node.b, x, y + h * r, w, h * (1 - r));
      }
    };
    walk(tree, 0, 0, 1, 1);

    // Hide panes that are no longer in the tree (host should also call closePane, but we belt-and-suspenders).
    const keepIds = new Set(placements.map((p) => p.paneId));
    for (const id of Array.from(panes.keys())) {
      if (!keepIds.has(id)) closePane(id);
    }

    for (const p of placements) {
      let entry = panes.get(p.paneId);
      if (!entry) entry = createPane(p.paneId);
      entry.el.style.left = (p.x * 100).toFixed(4) + "%";
      entry.el.style.top = (p.y * 100).toFixed(4) + "%";
      entry.el.style.width = (p.w * 100).toFixed(4) + "%";
      entry.el.style.height = (p.h * 100).toFixed(4) + "%";
      entry.el.style.inset = "auto";   // remove the ":inset: 0" default once positioned
      entry.el.classList.toggle("is-focused", p.paneId === activePane);
      try { entry.fit.fit(); } catch { /* layout not stable yet */ }
    }

    if (activePane) {
      const fe = panes.get(activePane);
      if (fe) fe.term.focus();
    }

    setStatus(
      placements.length === 1
        ? `pane ${activePane?.slice(0, 6) ?? "?"}`
        : `${placements.length} panes  ·  focus ${activePane?.slice(0, 6) ?? "?"}`);
  };

  const handleMessage = (raw) => {
    let msg;
    try { msg = typeof raw === "string" ? JSON.parse(raw) : raw; }
    catch { return; }
    if (!msg || typeof msg !== "object") return;

    switch (msg.type) {
      case "openPane":
        createPane(msg.paneId, msg.theme);
        break;
      case "closePane":
        closePane(msg.paneId);
        break;
      case "layout":
        applyLayout(msg.tree, msg.focused);
        break;
      case "output": {
        const e = panes.get(msg.paneId);
        if (e) e.term.write(b64ToBytes(msg.b64));
        break;
      }
      case "exit": {
        const e = panes.get(msg.paneId);
        if (!e) return;
        e.term.write(`\r\n\x1b[2m[process exited with code ${msg.code}]\x1b[0m\r\n`);
        break;
      }
      case "status":
        setStatus(msg.text);
        break;
      case "clear":
        clearPane(msg.paneId);
        break;
      case "fontSize": {
        const e = panes.get(msg.paneId ?? activePane);
        adjustFontSize(e, msg.delta, msg.size);
        break;
      }
      case "focusTerm":
        focusActiveTerm();
        break;
    }
  };

  if (window.chrome?.webview) {
    window.chrome.webview.addEventListener("message", (e) => handleMessage(e.data));
  } else {
    setStatus("Not running inside WebView2.");
  }

  window.addEventListener("resize", () => {
    if (lastTree) applyLayout(lastTree, activePane);
  });

  post({ type: "ready" });
  setStatus("Ready, waiting for host…");
})();
