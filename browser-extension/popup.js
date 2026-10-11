"use strict";
const status = document.getElementById("status"), auto = document.getElementById("auto");
auto.addEventListener("change", () => chrome.runtime.sendMessage({ kind: "autoRefresh", enabled: auto.checked }).catch(() => { status.textContent = "Tillägget behöver laddas om."; }));
async function update() {
  const value = await chrome.runtime.sendMessage({ kind: "status" });
  if (value.autoRefresh !== undefined) auto.checked = value.autoRefresh;
  status.textContent = value.status?.message ?? "Öppna Billing och anslut den här fliken.";
}
for (const kind of ["open", "connect", "read", "disconnect"]) {
  document.getElementById(kind).addEventListener("click", async () => {
    try {
      const result = await chrome.runtime.sendMessage({ kind, autoRefresh: auto.checked });
      if (!result.ok) { status.textContent = result.message ?? "Öppna Billing och anslut fliken först."; return; }
      await update();
    } catch { status.textContent = "Tillägget behöver laddas om."; }
  });
}
update();
