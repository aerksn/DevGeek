// Reads only TypeSafe Billing's visible Available credits card. No requests or credentials.
(function (scope) {
  "use strict";
  const clean = text => String(text ?? "").replace(/\s+/g, " ").trim();
  function isBilling(url) {
    try {
      const value = new URL(url);
      return value.protocol === "https:" && value.hostname === "console.typesafe.ai" &&
        (value.port === "" || value.port === "443") && /^\/settings\/billing\/?$/.test(value.pathname);
    } catch { return false; }
  }
  function dollars(text) {
    const match = /^(?:US\$|\$)\s*(-?(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d{1,2})?)$/.exec(clean(text));
    if (!match) return null;
    const value = Number(match[1].replaceAll(",", ""));
    return Number.isFinite(value) && Math.abs(value) < 1000000000 ? value.toFixed(2) : null;
  }
  function canRefresh(document) {
    if (document.activeElement?.matches("input,textarea,select,[contenteditable=true]")) return false;
    // Billing itself is TypeSafe's Settings dialog. Extra dialogs are payment/sign-in work.
    return [...document.querySelectorAll('[role=dialog],dialog[open]')].every(dialog =>
      dialog.getClientRects().length === 0 || dialog.matches('[role=dialog][aria-label="Settings"]') &&
      [...dialog.querySelectorAll("h3")].some(heading => clean(heading.textContent) === "Available credits"));
  }
  function read(document, url) {
    if (!isBilling(url)) return { schema: 1, state: "paused" };
    const visible = element => element.getClientRects().length > 0 &&
      document.defaultView.getComputedStyle(element).visibility !== "hidden";
    const headings = [...document.querySelectorAll("h3")].filter(element =>
      visible(element) && clean(element.textContent) === "Available credits");
    // Fail closed on multiple cards, including organization/account transition states.
    if (headings.length !== 1) return { schema: 1, state: "unreadable" };
    const card = headings[0].closest("section");
    if (!card) return { schema: 1, state: "unreadable" };
    // The amount is a span in the card. Exclude payment controls, hints and history.
    const fields = [...card.querySelectorAll("span")].filter(element => visible(element) &&
      !element.closest("button,a,p,table,[role=tooltip],[role=alert]") &&
      !element.querySelector("span") && /^(?:US\$|\$)/.test(clean(element.innerText)));
    const amount = fields.length === 1 ? dollars(fields[0].innerText) : null;
    return amount !== null ? { schema: 1, state: "ok", amount, currency: "USD" }
      : { schema: 1, state: "unreadable" };
  }
  const api = { isBilling, dollars, canRefresh, read };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else scope.OtterMonitorJevBalanceReader = api;
})(globalThis);
