// Read rendered plan cards only. No requests, cookies, tokens or private page state.
(function (scope) {
  "use strict";
  const clean = value => String(value ?? "").replace(/\s+/g, " ").trim().toLowerCase();
  const session = new Set(["current session", "aktuell session", "nuvarande session"]);
  const weekly = new Set(["all models", "alla modeller"]);
  const weeklyFallback = new Set(["weekly limits", "veckogränser"]);
  const isLabel = text => session.has(text) || weekly.has(text) || weeklyFallback.has(text);
  function isUsage(url) {
    try { const u = new URL(url); return u.protocol === "https:" && u.hostname === "claude.ai" && (!u.port || u.port === "443") && u.pathname.replace(/\/$/, "") === "/settings/usage"; }
    catch { return false; }
  }
  function read(document, url) {
    if (!isUsage(url)) {
      const u = new URL(url);
      return { state: u.pathname.includes("login") || u.hostname !== "claude.ai" ? "login_required" : "paused" };
    }
    const visible = e => e.getClientRects().length > 0 && document.defaultView.getComputedStyle(e).visibility !== "hidden";
    const labels = [...document.querySelectorAll("h1,h2,h3,h4,p,span,div,label")]
      .filter(e => visible(e) && isLabel(clean(e.textContent)));
    function card(label) {
      for (let e = label.parentElement, depth = 0; e && depth < 5; e = e.parentElement, depth++) {
        const text = clean(e.innerText);
        if (text.length > 1200 || labels.some(other => other !== label && e.contains(other) && clean(other.textContent) !== clean(label.textContent))) break;
        const values = new Set();
        for (const match of text.matchAll(/(\d+(?:[.,]\d+)?)\s*%\s*(?:used|använt|använd|utnyttjat)\b/g)) values.add(Number(match[1].replace(",", ".")));
        // Accessible progressbar fallback requires an explicit 0..100 scale.
        for (const bar of e.querySelectorAll('[role="progressbar"]')) {
          if (!visible(bar)) continue;
          const min = Number(bar.getAttribute("aria-valuemin") ?? 0), max = Number(bar.getAttribute("aria-valuemax") ?? 100);
          const raw = bar.getAttribute("aria-valuenow");
          if (raw !== null && min === 0 && max === 100 && raw.trim()) values.add(Number(raw));
        }
        if (values.size > 1) return null;
        if (values.size === 1) {
          const usedPercent = [...values][0];
          if (!Number.isFinite(usedPercent) || usedPercent < 0 || usedPercent > 100) return null;
          const time = e.querySelector("time[datetime]");
          const reset = time ? Date.parse(time.getAttribute("datetime")) : NaN;
          return { usedPercent, ...(Number.isFinite(reset) ? { resetsAt: Math.floor(reset / 1000) } : {}) };
        }
      }
      return null;
    }
    const windows = [];
    for (const [minutes, names] of [[300, session], [10080, labels.some(e => weekly.has(clean(e.textContent))) ? weekly : weeklyFallback]]) {
      const matches = labels.filter(e => names.has(clean(e.textContent))).map(card).filter(Boolean);
      const distinct = new Map(matches.map(value => [JSON.stringify(value), value]));
      if (distinct.size > 1) return { state: "unreadable" };
      if (distinct.size === 1) windows.push({ minutes, ...distinct.values().next().value });
    }
    return windows.length ? { state: "ok", windows } : { state: "unreadable" };
  }
  if (typeof module !== "undefined") module.exports = { read, isUsage };
  else scope.PortPilotClaudeUsage = { read, isUsage };
})(typeof globalThis !== "undefined" ? globalThis : this);
