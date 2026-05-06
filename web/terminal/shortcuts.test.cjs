const assert = require("node:assert/strict");
const test = require("node:test");

const { resolveShortcut } = require("./shortcuts.js");

const key = (overrides) => ({
  type: "keydown",
  key: "",
  ctrlKey: false,
  altKey: false,
  shiftKey: false,
  metaKey: false,
  ...overrides,
});

test("maps command palette shortcut", () => {
  assert.equal(resolveShortcut(key({ ctrlKey: true, shiftKey: true, key: "P" })), "paletteToggle");
  assert.equal(resolveShortcut(key({ ctrlKey: true, shiftKey: true, key: "p" })), "paletteToggle");
});

test("maps pane focus shortcuts", () => {
  assert.equal(resolveShortcut(key({ ctrlKey: true, altKey: true, key: "ArrowRight" })), "focusNext");
  assert.equal(resolveShortcut(key({ ctrlKey: true, altKey: true, key: "ArrowLeft" })), "focusPrevious");
});

test("maps split shortcuts", () => {
  assert.equal(resolveShortcut(key({ ctrlKey: true, altKey: true, shiftKey: true, key: "ArrowRight" })), "splitRight");
  assert.equal(resolveShortcut(key({ ctrlKey: true, altKey: true, shiftKey: true, key: "ArrowDown" })), "splitDown");
});

test("maps send-to-pane shortcut", () => {
  assert.equal(resolveShortcut(key({ ctrlKey: true, altKey: true, key: "s" })), "sendToPane");
  assert.equal(resolveShortcut(key({ ctrlKey: true, altKey: true, key: "S" })), "sendToPane");
});

test("passes ordinary terminal input through", () => {
  assert.equal(resolveShortcut(key({ ctrlKey: true, key: "c" })), null);
  assert.equal(resolveShortcut(key({ type: "keyup", ctrlKey: true, altKey: true, key: "ArrowRight" })), null);
});
