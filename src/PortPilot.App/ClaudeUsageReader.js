// Read rendered plan cards only. No requests, cookies, tokens or private page state.
(function (scope) {
  "use strict";
  const clean = value => String(value ?? "").replace(/\s+/g, " ").trim().toLowerCase();
  const session = new Set(["current session", "aktuell session", "nuvarande session"]);
  const weekly = new Set(["all models", "alla modeller"]);
  const weeklyFallback = new Set(["weekly limits", "veckogränser"]);
  const otherBudgets = new Set(["fable", "sonnet only", "opus only", "haiku only", "extra usage", "usage credits", "cloud session credits", "included credit", "limit resets"]);
  const isLabel = text => session.has(text) || weekly.has(text) || weeklyFallback.has(text);
  function isUsage(url) {
    try { const u = new URL(url); return u.protocol === "https:" && u.hostname === "claude.ai" && (!u.port || u.port === "443") && u.pathname.replace(/\/$/, "") === "/settings/usage"; }
    catch { return false; }
  }
  function read(document, url) {
    if (!isUsage(url)) {
      try { const u = new URL(url); return { state: u.pathname.includes("login") || u.hostname !== "claude.ai" ? "login_required" : "paused" }; }
      catch { return { state: "unreadable" }; }
    }
    const visible = e => e.getClientRects().length > 0 && document.defaultView.getComputedStyle(e).visibility !== "hidden";
    // Usage is now a settings dialog. Keep the quota reader out of the chat sidebar.
    const dialogs = [...document.querySelectorAll('[role="dialog"]')].filter(visible);
    const root = dialogs.find(e => /plan usage limits|current session|all models|aktuell session|alla modeller/.test(clean(e.innerText))) ?? document;
    const elements = [...root.querySelectorAll("h1,h2,h3,h4,h5,h6,p,span,div,label,strong,dt")].filter(visible);
    const labelled = elements.filter(e => isLabel(clean(e.textContent)));
    const labels = labelled.filter(e => !labelled.some(other => other !== e && e.contains(other) && clean(other.textContent) === clean(e.textContent)));
    const boundaries = elements.filter(e => isLabel(clean(e.textContent)) || otherBudgets.has(clean(e.textContent)));
    function card(label) {
      for (let e = label.parentElement, depth = 0; e && depth < 10; e = e.parentElement, depth++) {
        const text = clean(e.innerText);
        const ownName = clean(label.textContent);
        if (text.length > 1800 || boundaries.some(other => e.contains(other) && clean(other.textContent) !== ownName
          && !(weekly.has(ownName) && weeklyFallback.has(clean(other.textContent))))) break;
        const values = new Set();
        const percent = "(?<![\\d.,+-])([+-]?\\d+(?:[.,]\\d+)?)\\s*%";
        const used = "(?:used|använt|använd|utnyttjat)";
        const remaining = "(?:remaining|left|kvar|återstår)";
        for (const pattern of [new RegExp(percent + "\\s*" + used + "\\b", "g"), new RegExp("\\b" + used + "\\s*" + percent, "g")])
          for (const match of text.matchAll(pattern)) values.add(Number(match[1].replace(",", ".")));
        for (const match of text.matchAll(new RegExp(percent + "\\s*" + remaining + "\\b", "g"))) values.add(100 - Number(match[1].replace(",", ".")));
        // Native <progress> and accessible meters can expose a 0..1 or 0..100 scale.
        for (const bar of e.querySelectorAll('[role="progressbar"],progress')) {
          if (!visible(bar)) continue;
          const native = bar.tagName === "PROGRESS";
          const min = Number(bar.getAttribute("aria-valuemin") ?? 0), max = Number(bar.getAttribute(native ? "max" : "aria-valuemax") ?? (native ? 1 : 100));
          const raw = bar.getAttribute(native ? "value" : "aria-valuenow");
          if (raw !== null && raw.trim() && min === 0 && (max === 1 || max === 100)) values.add(Number((Number(raw) * 100 / max).toFixed(6)));
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
