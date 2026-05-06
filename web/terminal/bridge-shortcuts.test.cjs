const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const { TextEncoder } = require("node:util");

function makeElement() {
  return {
    className: "",
    dataset: {},
    style: {},
    textContent: "",
    title: "",
    children: [],
    listeners: new Map(),
    classList: {
      toggle() {},
    },
    appendChild(child) {
      this.children.push(child);
    },
    addEventListener(type, handler) {
      this.listeners.set(type, handler);
    },
    remove() {
      this.removed = true;
    },
  };
}

function createBridgeHarness() {
  const posts = [];
  const terminals = [];
  const status = makeElement();
  const paneRoot = makeElement();
  let inboundHandler = null;

  class FakeTerminal {
    constructor() {
      this.options = {};
      terminals.push(this);
    }

    loadAddon() {}

    attachCustomKeyEventHandler(handler) {
      this.keyHandler = handler;
    }

    open() {}
    onData() {}
    onBinary() {}
    onResize() {}
    write(_data, callback) { callback?.(); }
    dispose() {}
    clear() {}
    focus() {}
  }

  const context = {
    console,
    TextEncoder,
    Terminal: FakeTerminal,
    FitAddon: { FitAddon: class { fit() {} } },
    WebLinksAddon: { WebLinksAddon: class {} },
    performance: { now: () => 0 },
    atob: (value) => Buffer.from(value, "base64").toString("binary"),
    btoa: (value) => Buffer.from(value, "binary").toString("base64"),
    document: {
      getElementById(id) {
        if (id === "status") return status;
        if (id === "pane-root") return paneRoot;
        return null;
      },
      createElement: () => makeElement(),
    },
    chrome: {
      webview: {
        postMessage(value) {
          posts.push(JSON.parse(value));
        },
        addEventListener(type, handler) {
          if (type === "message") inboundHandler = handler;
        },
      },
    },
    addEventListener() {},
  };
  context.window = context;

  vm.createContext(context);
  vm.runInContext(
    fs.readFileSync(path.join(__dirname, "shortcuts.js"), "utf8"),
    context,
    { filename: "shortcuts.js" });
  vm.runInContext(
    fs.readFileSync(path.join(__dirname, "bridge.js"), "utf8"),
    context,
    { filename: "bridge.js" });

  assert.equal(typeof inboundHandler, "function");

  return {
    posts,
    terminals,
    sendHostMessage(message) {
      inboundHandler({ data: JSON.stringify(message) });
    },
  };
}

const key = (overrides) => ({
  type: "keydown",
  key: "",
  ctrlKey: false,
  altKey: false,
  shiftKey: false,
  metaKey: false,
  preventDefault() {
    this.prevented = true;
  },
  ...overrides,
});

test("bridge posts shortcut host commands from xterm key events", () => {
  const harness = createBridgeHarness();
  harness.sendHostMessage({ type: "openPane", paneId: "pane-a" });

  const handler = harness.terminals[0].keyHandler;
  const cases = [
    [key({ ctrlKey: true, shiftKey: true, key: "p" }), "paletteToggle"],
    [key({ ctrlKey: true, altKey: true, shiftKey: true, key: "ArrowRight" }), "splitRight"],
    [key({ ctrlKey: true, altKey: true, shiftKey: true, key: "ArrowDown" }), "splitDown"],
    [key({ ctrlKey: true, altKey: true, key: "ArrowRight" }), "focusNext"],
    [key({ ctrlKey: true, altKey: true, key: "ArrowLeft" }), "focusPrevious"],
    [key({ ctrlKey: true, altKey: true, key: "s" }), "sendToPane"],
  ];

  for (const [event, expectedType] of cases) {
    assert.equal(handler(event), false);
    assert.equal(event.prevented, true);
    assert.deepEqual(harness.posts.at(-1), { type: expectedType });
  }
});

test("bridge lets ordinary terminal input continue through xterm", () => {
  const harness = createBridgeHarness();
  harness.sendHostMessage({ type: "openPane", paneId: "pane-a" });

  const before = harness.posts.length;
  assert.equal(harness.terminals[0].keyHandler(key({ key: "a" })), true);
  assert.equal(harness.posts.length, before);
});
