"use strict";
importScripts("reader.js");
const HOST = "com.portpilot.openai_balance";
const BILLING = "https://platform.openai.com/settings/organization/billing/overview";
let queue = Promise.resolve();
async function status(message, ok = false) {
  await chrome.storage.session.set({ status: { message, ok, time: Date.now() } });
  await chrome.action.setBadgeText({ text: ok ? "" : "!" });
  await chrome.action.setBadgeBackgroundColor({ color: "#8764ad" });
}
function send(value) {
  // Serialize writes, including across different event handlers.
  const operation = queue.catch(() => {}).then(async () => {
    try {
      const response = await chrome.runtime.sendNativeMessage(HOST, value);
      if (!response?.ok) { await status(response?.error === "disabled" ? "Aktivera kopplingen i PortPilot först." : response?.error === "background_mode" ? "PortPilot använder sin egen bakgrundsläsare. Tillägget behövs inte i det läget." : "PortPilot kunde inte spara avläsningen."); return false; }
      await status(value.state === "ok" ? "Saldot har skickats till PortPilot." : "Billing behöver kontrolleras. Senaste saldo är markerat som äldre.", value.state === "ok");
      return true;
    } catch { await status("Kopplingen saknas. Registrera webbläsarkopplingen i PortPilot."); return false; }
  });
  queue = operation; return operation;
}
async function selected() { return (await chrome.storage.session.get("selectedTabId")).selectedTabId; }
async function scan(tabId) {
  try { await chrome.tabs.sendMessage(tabId, { kind: "scan" }); }
  catch { await status("Ladda om Billing-fliken och anslut den igen."); }
}
chrome.runtime.onMessage.addListener((message, sender, reply) => {
  (async () => {
    if (sender.id !== chrome.runtime.id) return { ok: false };
    if (message.kind === "balance") {
      if (sender.frameId !== 0 || sender.tab?.id !== await selected()) return { ok: false };
      const input = message.value;
      if (input?.schema !== 1 || !["ok", "unreadable", "paused"].includes(input.state)) return { ok: false };
      if (!PortPilotBalanceReader.isBilling(sender.url) && !(input.state === "paused" && new URL(sender.url).origin === "https://platform.openai.com")) return { ok: false };
      const value = { schema: 1, state: input.state };
      if (input.state === "ok") {
        if (input.currency !== "USD" || typeof input.amount !== "string" || !/^-?\d{1,9}(\.\d{1,2})?$/.test(input.amount)) return { ok: false };
        value.amount = input.amount; value.currency = "USD";
      }
      return { ok: await send(value) };
    }
    if (sender.tab) return { ok: false }; // Popup commands must come from the extension's own page.
    if (message.kind === "open") { await chrome.tabs.create({ url: BILLING }); return { ok: true }; }
    if (message.kind === "connect") {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      if (!tab?.id || !PortPilotBalanceReader.isBilling(tab.url)) return { ok: false, message: "Öppna Billing-översikten i den aktiva fliken först." };
      await chrome.storage.session.set({ selectedTabId: tab.id });
      await chrome.storage.local.set({ connected: true, autoRefresh: message.autoRefresh === true });
      await scan(tab.id); return { ok: true };
    }
    if (message.kind === "read") { const id = await selected(); if (id !== undefined) await scan(id); return { ok: id !== undefined }; }
    if (message.kind === "autoRefresh") { await chrome.storage.local.set({ autoRefresh: message.enabled === true }); return { ok: true }; }
    if (message.kind === "disconnect") {
      await chrome.storage.session.remove("selectedTabId"); await chrome.storage.local.set({ connected: false });
      await send({ schema: 1, state: "paused" }); await status("Kopplingen är pausad."); return { ok: true };
    }
    if (message.kind === "status") return { ok: true, ...(await chrome.storage.session.get(["status", "selectedTabId"])), ...(await chrome.storage.local.get("autoRefresh")) };
    return { ok: false };
  })().then(reply, () => reply({ ok: false, message: "Avläsningen misslyckades." }));
  return true;
});
chrome.tabs.onRemoved.addListener(async id => {
  if (id !== await selected()) return;
  await chrome.storage.session.remove("selectedTabId"); await send({ schema: 1, state: "page_closed" });
});
chrome.tabs.onUpdated.addListener(async (id, changes) => {
  if (id !== await selected() || !changes.url) return;
  const url = new URL(changes.url);
  if (!PortPilotBalanceReader.isBilling(url.href)) await send({ schema: 1, state: url.hostname === "auth.openai.com" || url.pathname.startsWith("/login") ? "login_required" : "paused" });
});
chrome.alarms.onAlarm.addListener(async alarm => {
  if (alarm.name !== "billing-refresh") return;
  const id = await selected(); if (id === undefined) return;
  try {
    const tab = await chrome.tabs.get(id);
    const { autoRefresh } = await chrome.storage.local.get("autoRefresh");
    // Never interrupt a foreground tab, a payment dialog or form input.
    if (autoRefresh && !tab.active && PortPilotBalanceReader.isBilling(tab.url)) {
      const result = await chrome.tabs.sendMessage(id, { kind: "canReload" });
      if (result?.safe) await chrome.tabs.reload(id);
    }
  } catch { await status("Öppna Billing-översikten och anslut fliken igen."); }
});
async function alarms() { await chrome.alarms.create("billing-refresh", { periodInMinutes: 5 }); }
chrome.runtime.onInstalled.addListener(alarms);
chrome.runtime.onStartup.addListener(async () => {
  await alarms();
  const { connected } = await chrome.storage.local.get("connected");
  if (!connected) return;
  const tabs = (await chrome.tabs.query({ url: ["https://platform.openai.com/settings/organization/billing/*", "https://platform.openai.com/account/billing/*"] })).filter(tab => PortPilotBalanceReader.isBilling(tab.url));
  if (tabs.length === 1) { await chrome.storage.session.set({ selectedTabId: tabs[0].id }); await scan(tabs[0].id); }
  else await status("Välj en Billing-flik och klicka Anslut denna flik.");
});
