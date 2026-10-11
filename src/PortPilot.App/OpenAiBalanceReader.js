// Pure DOM reader. No cookies, storage, requests or account/session tokens are accessed.
(function (scope) {
  "use strict";
  const labels = new Set(["api credit balance", "credit balance"]);
  const clean = text => String(text ?? "").replace(/\s+/g, " ").trim();
  function isBilling(url) {
    try {
      const value = new URL(url);
      return value.protocol === "https:" && value.hostname === "platform.openai.com" && (value.port === "" || value.port === "443") &&
        /^(?:\/settings\/organization|\/account)\/billing(?:\/overview)?\/?$/.test(value.pathname);
    } catch { return false; }
  }
  function dollars(text) {
    if (/\b(?:EUR|CAD|AUD|GBP|SEK)\b|\b(?:CA|AU|C|A|NZ|HK|SG)\$|[€£]/i.test(text)) return [];
    const found = [];
    for (const match of text.matchAll(/(-?)\s*(?:US\$|\$)\s*(-?[\d,.]+)(?![\dA-Za-z.,])/g)) {
      let number = match[2];
      if (match[1] === "-" && !number.startsWith("-")) number = "-" + number;
      if (!/^-?(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d{1,2})?$/.test(number)) continue;
      number = number.replaceAll(",", "");
      const value = Number(number);
      if (Number.isFinite(value) && Math.abs(value) < 1000000000) found.push(value.toFixed(2));
    }
    return [...new Set(found)];
  }
  function read(document, url) {
    if (!isBilling(url)) return { schema: 1, state: "paused" };
    const visible = element => element.getClientRects().length > 0 && document.defaultView.getComputedStyle(element).visibility !== "hidden";
    const values = new Set();
    const candidates = document.querySelectorAll("span,p,h1,h2,h3,h4,dt,label");
    for (const element of candidates) {
      if (!labels.has(clean(element.textContent).toLowerCase()) || !visible(element)) continue;
      let parent = element.parentElement;
      for (let depth = 0; parent && depth < 5; depth++, parent = parent.parentElement) {
        const text = clean(parent.innerText);
        if (text.length > 160 || parent.querySelector("button,a,h1,h2,h3,[role=alert],input,select")) break;
        const amounts = dollars(text);
        if (amounts.length === 1) { values.add(amounts[0]); break; }
        if (amounts.length > 1) break; // Ambiguous card: fail instead of guessing.
        if (parent.tagName !== "SPAN" || !labels.has(text.toLowerCase())) break; // Stop at the first card; never wander into warnings.
      }
    }
    return values.size === 1 ? { schema: 1, state: "ok", amount: [...values][0], currency: "USD" }
      : { schema: 1, state: "unreadable" };
  }
  const api = { isBilling, dollars, read };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else scope.PortPilotBalanceReader = api;
})(globalThis);
