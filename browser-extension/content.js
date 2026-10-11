(() => {
  "use strict";
  let previous = "", timeout;
  async function scan(force = false) {
    const value = PortPilotBalanceReader.read(document, location.href);
    const fingerprint = JSON.stringify(value);
    if (!force && previous === fingerprint) return;
    previous = fingerprint;
    try { await chrome.runtime.sendMessage({ kind: "balance", value }); } catch { /* Extension may be reloaded. */ }
  }
  const observer = new MutationObserver(() => {
    clearTimeout(timeout); timeout = setTimeout(() => scan(), 600);
  });
  observer.observe(document.documentElement, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ["hidden", "style", "class"] });
  chrome.runtime.onMessage.addListener((message, _sender, reply) => {
    if (message.kind === "scan") { scan(true).then(() => reply({ ok: true })); return true; }
    if (message.kind === "canReload") {
      const editing = document.activeElement?.matches("input,textarea,select,[contenteditable=true]");
      reply({ safe: PortPilotBalanceReader.isBilling(location.href) && !editing && !document.querySelector('[role="dialog"],dialog[open]') });
    }
  });
  window.addEventListener("popstate", () => scan());
  // SPA URL transitions with no text mutation are detected without refreshing the balance timestamp.
  let route = location.href;
  setInterval(() => { if (route !== location.href) { route = location.href; previous = ""; scan(); } }, 2000);
  scan();
})();
