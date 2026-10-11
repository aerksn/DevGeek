const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const reader = require('../reader.js');
const billing = 'https://platform.openai.com/settings/organization/billing/overview';

test('USD amounts: zero, negative and thousands; malformed/foreign values rejected', () => {
  for (const [text, expected] of [['$0', '0.00'], ['$24.50', '24.50'], ['-$0.50', '-0.50'], ['$-2.75', '-2.75'], ['US$1,234.56', '1234.56']]) assert.deepEqual(reader.dollars(text), [expected]);
  for (const text of ['$1,23', '$1.234', '$1,234,56', '$1e3', 'CA$7.20', 'AU$7.20', 'EUR $7.20', 'CAD $7', '€7', '$1000000000']) assert.deepEqual(reader.dollars(text), []);
});
test('Only the real Billing overview origin/route is readable', () => {
  assert.equal(reader.isBilling(billing), true);
  assert.equal(reader.isBilling(billing + '?test=1'), true);
  for (const url of ['http://platform.openai.com/account/billing', 'https://platform.openai.com:8443/account/billing', 'https://evil.test/account/billing', 'https://platform.openai.com.evil.test/account/billing', 'https://platform.openai.com/settings/organization/billing/history', 'not-url']) assert.equal(reader.isBilling(url), false);
});
// A DOM fixture matching the observed label/span and sibling amount card. Warnings live outside it.
class Element {
  constructor(tag, text = '', children = [], hidden = false) { this.tag = tag; this.tagName = tag.toUpperCase(); this.text = text; this.children = children; this.hidden = hidden; children.forEach(child => child.parentElement = this); }
  get textContent() { return this.text + this.children.map(child => child.textContent).join(' '); }
  get innerText() { return this.hidden ? '' : this.text + this.children.filter(child => !child.hidden).map(child => child.innerText).join(' '); }
  getClientRects() { return this.hidden || this.parentElement?.getClientRects().length === 0 ? [] : [{}]; }
  querySelector() { return this.children.flatMap(child => [child, ...child.descendants()]).find(child => ['button', 'a', 'h1', 'h2', 'h3', 'input', 'select'].includes(child.tag)); }
  descendants() { return this.children.flatMap(child => [child, ...child.descendants()]); }
}
const el = (tag, text, children, hidden) => new Element(tag, text, children, hidden);
function card(amount = '$24.50', hidden = false) { return el('div', '', [el('span', '', [el('span', 'API credit balance')]), el('div', amount)], hidden); }
function document(...children) { const root = el('div', '', children); return { querySelectorAll: selector => root.descendants().filter(element => selector.split(',').includes(element.tag)), defaultView: { getComputedStyle: element => ({ visibility: element.hidden ? 'hidden' : 'visible' }) } }; }
test('Exact balance card wins over $0 warning and Buy credits button', () => {
  assert.deepEqual(reader.read(document(card(), el('p', 'When your credit reaches $0, requests stop.'), el('button', 'Buy credits')), billing), { schema: 1, state: 'ok', amount: '24.50', currency: 'USD' });
});
test('Hidden, conflicting, missing or malformed balance fields never become a guessed amount', () => {
  for (const doc of [document(card(''), el('p', 'Your credit reaches $0')), document(card('', false), el('p', 'Your credit reaches $0'), el('button', 'Buy credits')), document(card('$7 $8')), document(card('$7'), card('$8')), document(card('$7', true)), document(card('Loading…'), el('p', '$0')), document(card('EUR $7'))]) assert.deepEqual(reader.read(doc, billing), { schema: 1, state: 'unreadable' });
  assert.deepEqual(reader.read(document(card()), 'https://platform.openai.com/home'), { schema: 1, state: 'paused' });
});
test('Manifest uses the stable host identity and limits page permissions to Billing', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'manifest.json')));
  const id = [...crypto.createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest().subarray(0, 16)].map(byte => String.fromCharCode(97 + (byte >> 4), 97 + (byte & 15))).join('');
  assert.equal(id, 'dpfmlcmepinoenieelepgijlkghleeld');
  assert.deepEqual(manifest.permissions.sort(), ['alarms', 'nativeMessaging', 'storage']);
  assert.deepEqual(manifest.host_permissions, ['https://platform.openai.com/settings/organization/billing/*', 'https://platform.openai.com/account/billing/*']);
});
function harness() {
  const local = {}, session = {}, handlers = {}, native = [], reloads = [];
  const storage = data => ({ get: async keys => typeof keys === 'string' ? { [keys]: data[keys] } : Object.fromEntries(keys.map(key => [key, data[key]])), set: async value => Object.assign(data, value), remove: async key => { delete data[key]; } });
  const event = name => ({ addListener: listener => { handlers[name] = listener; } });
  const tab = { id: 10, active: true, url: billing };
  const chrome = {
    runtime: { id: 'fixture-extension', onMessage: event('message'), onInstalled: event('installed'), onStartup: event('startup'), sendNativeMessage: async (host, value) => { native.push({ host, value }); return { ok: true }; } },
    storage: { local: storage(local), session: storage(session) }, action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {} },
    tabs: { query: async () => [tab], get: async () => tab, sendMessage: async (_id, value) => ({ safe: value.kind === 'canReload', ok: true }), create: async () => {}, reload: async id => reloads.push(id), onRemoved: event('removed'), onUpdated: event('updated') },
    alarms: { create: async () => {}, onAlarm: event('alarm') }
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '..', 'worker.js'), 'utf8'), { chrome, importScripts: () => {}, PortPilotBalanceReader: reader, URL, Date });
  const message = (value, sender = { id: chrome.runtime.id }) => new Promise(resolve => handlers.message(value, sender, resolve));
  return { local, session, handlers, native, reloads, tab, message, sender: { id: chrome.runtime.id, frameId: 0, tab: { id: 10 }, url: billing } };
}
test('Worker rejects unselected tabs, frames and foreign senders and forwards only normalized money', async () => {
  const h = harness(), value = { kind: 'balance', value: { schema: 1, state: 'ok', amount: '24.50', currency: 'USD', secret: 'fixture-only' } };
  assert.equal((await h.message(value, h.sender)).ok, false);
  await h.message({ kind: 'connect', autoRefresh: true });
  for (const sender of [{ ...h.sender, frameId: 1 }, { ...h.sender, tab: { id: 20 } }, { ...h.sender, id: 'foreign' }, { ...h.sender, url: 'https://evil.test/account/billing' }]) assert.equal((await h.message(value, sender)).ok, false);
  assert.equal(h.native.length, 0);
  assert.equal((await h.message(value, h.sender)).ok, true);
  assert.equal(JSON.stringify(h.native[0]), JSON.stringify({ host: 'com.portpilot.openai_balance', value: { schema: 1, state: 'ok', amount: '24.50', currency: 'USD' } }));
  assert.equal(JSON.stringify(h.session).includes('24.50'), false);
});
test('Automatic refresh skips active tabs and pause/close mark the last reading unavailable', async () => {
  const h = harness(); await h.message({ kind: 'connect', autoRefresh: true });
  await h.handlers.alarm({ name: 'billing-refresh' }); assert.deepEqual(h.reloads, []);
  h.tab.active = false; await h.handlers.alarm({ name: 'billing-refresh' }); assert.deepEqual(h.reloads, [10]);
  await h.message({ kind: 'autoRefresh', enabled: false }); await h.handlers.alarm({ name: 'billing-refresh' }); assert.deepEqual(h.reloads, [10]);
  await h.handlers.removed(10); assert.equal(h.native.at(-1).value.state, 'page_closed');
  await h.message({ kind: 'disconnect' }); assert.equal(h.native.at(-1).value.state, 'paused');
});
