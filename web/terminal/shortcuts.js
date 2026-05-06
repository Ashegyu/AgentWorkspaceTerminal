// Shared terminal shortcut resolver.
//
// Browser: exposes window.AgentWorkspaceShortcuts.resolveShortcut.
// Node tests: exposes module.exports.resolveShortcut.
(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  }
  if (root) {
    root.AgentWorkspaceShortcuts = api;
  }
})(typeof globalThis !== "undefined" ? globalThis : undefined, function () {
  "use strict";

  const isKey = (e, value) => String(e.key || "").toLowerCase() === value;
  const isArrow = (e, value) => e.key === value;

  function resolveShortcut(e) {
    if (!e || e.type !== "keydown") return null;

    if (e.ctrlKey && e.shiftKey && !e.altKey && !e.metaKey && isKey(e, "p")) {
      return "paletteToggle";
    }

    if (!e.ctrlKey || !e.altKey || e.metaKey) return null;

    if (e.shiftKey && isArrow(e, "ArrowRight")) return "splitRight";
    if (e.shiftKey && isArrow(e, "ArrowDown")) return "splitDown";

    if (!e.shiftKey && isArrow(e, "ArrowRight")) return "focusNext";
    if (!e.shiftKey && isArrow(e, "ArrowLeft")) return "focusPrevious";
    if (!e.shiftKey && isKey(e, "s")) return "sendToPane";

    return null;
  }

  return { resolveShortcut };
});
