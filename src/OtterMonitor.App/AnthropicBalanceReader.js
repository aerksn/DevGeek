// Read only the visible credit-balance card. No requests, cookies or page APIs.
(function (scope) {
  "use strict";
  const clean = text => String(text ?? "").replace(/\s+/g, " ").trim();
  const labels = new Set(["credit balance", "available credits", "remaining credits", "remaining balance"]);
  function isBilling(url) {
    try {
      const value = new URL(url);
      return value.protocol === "https:" && ["platform.claude.com", "console.anthropic.com"].includes(value.hostname)
        && (!value.port || value.port === "443") && /^\/settings\/billing\/?$/.test(value.pathname);
    } catch { return false; }
  }
  function dollars(text) {
    if (/\b(?:EUR|CAD|AUD|GBP|SEK)\b|\b(?:CA|AU|C|A|NZ|HK|SG)\$|[€£]/i.test(text)) return [];
    const values = [];
    for (const match of text.matchAll(/(-?)\s*(?:US\$|\$)\s*(-?[\d,.]+)(?![\dA-Za-z.,])/g)) {
      let number = match[2];
      if (match[1] === "-" && !number.startsWith("-")) number = "-" + number;
      if (!/^-?(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d{1,2})?$/.test(number)) continue;
      const value = Number(number.replaceAll(",", ""));
      if (Number.isFinite(value) && Math.abs(value) < 1000000000) values.push(value.toFixed(2));
    }
    return [...new Set(values)];
  }
  function read(document, url) {
    if (!isBilling(url)) return { schema: 1, state: "paused" };
    const visible = element => element.getClientRects().length > 0 && document.defaultView.getComputedStyle(element).visibility !== "hidden";
    // The current Console shows purchased and promotional credits separately in the card.
    // Its header's monetary Billing link is the provider's own total, not a sum we calculate.
    const totals = new Set();
    let totalLinkSeen = false;
    for (const link of document.querySelectorAll("header a[href],[role=banner] a[href]")) {
      if (!visible(link)) continue;
      let target;
      try { target = new URL(link.getAttribute("href"), url).href; } catch { continue; }
      if (!isBilling(target)) continue;
      const text = clean(link.innerText);
      if (!/^(?:-?\s*)?(?:US\$|\$)\s*-?[\d,.]+(?:\s*USD)?$/.test(text)) continue;
      totalLinkSeen = true;
      const amounts = dollars(text);
      if (amounts.length !== 1) return { schema: 1, state: "unreadable" };
      totals.add(amounts[0]);
    }
    if (totalLinkSeen) return totals.size === 1 ? { schema: 1, state: "ok", amount: [...totals][0], currency: "USD" }
      : { schema: 1, state: "unreadable" };
    const values = new Set();
    let ambiguous = false;
    for (const label of document.querySelectorAll("span,p,h1,h2,h3,h4,dt,label,div")) {
      if (!labels.has(clean(label.textContent).toLowerCase()) || !visible(label)) continue;
      if ([...label.children].some(child => labels.has(clean(child.textContent).toLowerCase()))) continue;
      for (let parent = label.parentElement, depth = 0; parent && depth < 4; parent = parent.parentElement, depth++) {
        // Inspect text nodes without buttons, hidden text, expiry rows, invoices or auto-reload thresholds.
        const text = clean(parent.innerText);
        if (text.length > 220 || /auto.?reload|invoice|expir|when .*reach|buy credits|purchase/i.test(text)) break;
        const amounts = dollars(text);
        if (amounts.length > 1) { ambiguous = true; break; }
        if (amounts.length === 1) { values.add(amounts[0]); break; }
        if (!labels.has(text.toLowerCase()) || parent.children.length !== 1) break;
      }
    }
    return !ambiguous && values.size === 1 ? { schema: 1, state: "ok", amount: [...values][0], currency: "USD" }
      : { schema: 1, state: "unreadable" };
  }
  const api = { isBilling, dollars, read };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else scope.OtterMonitorAnthropicBalanceReader = api;
})(globalThis);
